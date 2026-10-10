using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Application.Telemetry;
using AppValidationException = LlmLab.Application.Common.ValidationException;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmLab.Infrastructure.Agents;

/// <summary>Alt ajan görevleri ayarları ("SubAgents" bölümü).</summary>
public sealed class SubAgentOptions
{
    public const string SectionName = "SubAgents";

    /// <summary>Aynı anda model çağıran alt ajan sayısı. Yerel tek GPU'da fazlası sırada bekler ve hepsini yavaşlatır.</summary>
    [Range(1, 8)] public int MaxConcurrent { get; set; } = 2;

    /// <summary>Çalışmakta olabilecek (bekleyenler dahil) en fazla görev.</summary>
    [Range(1, 64)] public int MaxActive { get; set; } = 8;

    /// <summary>Bir görevin en uzun süresi.</summary>
    [Range(10, 3600)] public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Koordinatöre verilen sonuç metninin en fazla uzunluğu (bağlamı şişirmesin).</summary>
    [Range(200, 50000)] public int MaxResultChars { get; set; } = 6000;

    /// <summary>Bellekte tutulan bitmiş görev sayısı (en eskiler atılır).</summary>
    [Range(1, 1000)] public int RetainFinished { get; set; } = 100;
}

/// <summary>
/// <see cref="ISubAgentRunner"/>'ın süreç içi uygulaması. Her görev arka planda, kendi DI kapsamında ve kendi iptal
/// belirteciyle çalışır; istek bitse de sürer. API yeniden başlarsa görevler kaybolur: bilinçli ödünleşim, faz 3'te
/// aynı port Dapr iş akışı + aktörleriyle uygulanır (bkz. ROADMAP).
/// </summary>
internal sealed class InMemorySubAgentRunner : ISubAgentRunner
{
    private const string CodeToolName = "run_python";

    /// <summary>Ajanın kod çalıştırma aracı olabilir mi? (İzin listesi boşsa tüm araçlar demektir.)</summary>
    private static bool CanRunCode(AgentProfile p) => p.AllowedTools.Count == 0 || p.AllowedTools.Contains(CodeToolName);

    private const string SubAgentRules =
        "Sana bir koordinatör ajan görev verdi. Görevi kendi başına, eksiksiz ve kısa bir cevapla tamamla; koordinatöre " +
        "soru sorma. Cevabın doğrudan sonuç olsun (giriş cümlesi, özür ya da tekrar yazma). Bir araç gerekiyorsa onu ÇAĞIR; " +
        "bir aracın çıktısını asla kendin yazma ya da tahmin etme. Kodu kullanıcı ya da koordinatör çalıştırmayacak: " +
        "\"aşağıdaki kodu çalıştırın\" gibi cümle yazma, kodu SEN çalıştır ve çıktıyı raporla. " +
        "Araç hata verirse (ör. kütüphane ya da işlev yok) hatayı oku, " +
        "düzelt ve aracı YENİDEN çağır; kod yazıp çalıştırmadan, \"yeniden denemek ister misiniz?\" diye sorarak ya da " +
        "yalnızca kod listesi vererek bitirme. Sonuçta yalnızca aracın gerçekten döndürdüğü çıktıyı raporla; " +
        "çalıştıramadıysan bunu açıkça söyle.";

    private sealed class Job(string id, string agentId, string task, DateTime startedAt)
    {
        private readonly object _gate = new();
        private readonly List<string> _toolCalls = [];

        public string Id { get; } = id;
        public string AgentId { get; } = agentId;
        public string Task { get; } = task;
        public DateTime StartedAt { get; } = startedAt;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile SubAgentStatus Status = SubAgentStatus.Running;
        public volatile bool StopRequested;
        public string? Result;
        public string? Error;
        public DateTime? FinishedAt;

        public void AddToolCall(string name)
        {
            lock (_gate) _toolCalls.Add(name);
        }

        public SubAgentSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SubAgentSnapshot(Id, AgentId, Task, Status, Result, Error, StartedAt, FinishedAt, _toolCalls.ToList());
            }
        }
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly SubAgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<InMemorySubAgentRunner> _log;
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _slots;

    public InMemorySubAgentRunner(
        IServiceScopeFactory scopes, IOptions<SubAgentOptions> options, TimeProvider clock,
        ILogger<InMemorySubAgentRunner> log)
    {
        _scopes = scopes;
        _options = options.Value;
        _clock = clock;
        _log = log;
        _slots = new SemaphoreSlim(_options.MaxConcurrent);
    }

    public async Task<SubAgentSnapshot> StartAsync(string agentId, string task, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new AppValidationException("Görev metni boş olamaz.");
        if (task.Length > 4000) throw new AppValidationException("Görev metni en fazla 4000 karakter olabilir.");
        if (_jobs.Values.Count(j => j.Status == SubAgentStatus.Running) >= _options.MaxActive)
            throw new AppValidationException($"Aynı anda en fazla {_options.MaxActive} alt ajan görevi çalışabilir; birini bekleyin ya da durdurun.");

        // Ajan kaydı doğrulanır: var, etkin ve başka ajana devretmeyen bir ajan olmalı (özyinelemeyi önler).
        AgentProfile profile;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
            var def = await repo.GetAsync(agentId, ct) ?? throw new NotFoundException($"Ajan bulunamadı: {agentId}");
            if (!def.Enabled) throw new AppValidationException($"'{def.DisplayName}' ajanı devre dışı.");
            if (def.CanDelegate) throw new AppValidationException("Koordinatör ajan alt ajan olarak çalıştırılamaz.");
            profile = AgentProfile.From(def);
        }

        var job = new Job(Guid.NewGuid().ToString("N")[..8], profile.Id, task.Trim(), _clock.GetUtcNow().UtcDateTime);
        _jobs[job.Id] = job;
        PruneFinished();

        // İş parçacığı havuzunda başlar ama mevcut izleme bağlamını (Activity) taşır: alt ajanın iç adımları
        // koordinatörün izinin altında görünür.
        _ = Task.Run(() => RunAsync(job, profile));

        return job.Snapshot();
    }

    public SubAgentSnapshot? Get(string taskId) => _jobs.TryGetValue(taskId, out var j) ? j.Snapshot() : null;

    public async Task<SubAgentSnapshot?> WaitAsync(string taskId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(taskId, out var job)) return null;
        if (job.Status == SubAgentStatus.Running && timeout > TimeSpan.Zero)
        {
            await Task.WhenAny(job.Done.Task, Task.Delay(timeout, ct));
        }
        return job.Snapshot();
    }

    public bool Stop(string taskId)
    {
        if (!_jobs.TryGetValue(taskId, out var job) || job.Status != SubAgentStatus.Running) return false;
        job.StopRequested = true;
        job.Cts.Cancel();
        return true;
    }

    public IReadOnlyList<SubAgentSnapshot> List() =>
        _jobs.Values.Select(j => j.Snapshot()).OrderBy(s => s.StartedAt).ToList();

    private async Task RunAsync(Job job, AgentProfile profile)
    {
        var sw = Stopwatch.StartNew();
        using var activity = LlmLabTelemetry.ActivitySource.StartActivity("subagent.run");
        activity?.SetTag("llmlab.subagent.id", job.Id);
        activity?.SetTag(LlmLabTelemetry.Tags.AgentId, profile.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await _slots.WaitAsync(timeout.Token);
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var chat = scope.ServiceProvider.GetRequiredService<IChatAgent>();

                // Alt ajan "Belgeler" özelliğiyle çalışır; hangi belge araçlarını görebileceğini ajanın izin listesi belirler.
                var message = job.Task;
                string? sessionState = null;
                var text = new StringBuilder();

                // En çok bir "kodu çalıştır" düzeltme turu: küçük model kodu yazıp ÇALIŞTIRMADAN bitirebiliyor ve
                // çıktıyı koordinatör uyduruyor (gözlendi). Güvence: kod listesi var ama run_python çağrılmadıysa bir kez hatırlat.
                for (var pass = 0; ; pass++)
                {
                    text.Clear();
                    var ranCode = false;
                    var request = new AgentRequest(message, sessionState, Think: false, UseDocuments: true, profile, SubAgentRules);
                    await foreach (var evt in chat.RunAsync(request, timeout.Token))
                    {
                        if (evt is TextDelta t) text.Append(t.Text);
                        else if (evt is ToolInvoked tool)
                        {
                            job.AddToolCall(tool.Name);
                            ranCode |= tool.Name == CodeToolName;
                        }
                        else if (evt is SessionSaved saved) sessionState = saved.State;
                    }

                    if (pass == 0 && !ranCode && CanRunCode(profile) && text.ToString().Contains("```", StringComparison.Ordinal))
                    {
                        _log.LogWarning("Alt ajan {Agent} kodu çalıştırmadan bitirdi; {Tool} çağırması hatırlatılıyor.", profile.Id, CodeToolName);
                        message = $"Kodu henüz ÇALIŞTIRMADIN; yazdığın çıktı gerçek değildir. Şimdi {CodeToolName} aracını çağırarak " +
                                  "kodu çalıştır (numpy gibi bir kütüphane yoksa standart kütüphaneyle) ve YALNIZCA aracın " +
                                  "döndürdüğü çıktıyı raporla.";
                        continue;
                    }
                    break;
                }

                var result = text.ToString().Trim();
                if (result.Length > _options.MaxResultChars)
                    result = result[.._options.MaxResultChars] + "\n[... sonuç uzunluk sınırı nedeniyle kısaltıldı]";
                job.Result = result.Length == 0 ? "(alt ajan boş cevap döndürdü)" : result;
                job.Status = SubAgentStatus.Completed;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (job.StopRequested)
            {
                job.Status = SubAgentStatus.Stopped;
            }
            else
            {
                job.Error = $"Zaman aşımı ({_options.TimeoutSeconds} sn).";
                job.Status = SubAgentStatus.Failed;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Alt ajan görevi başarısız: {Task} ({Agent})", job.Id, profile.Id);
            job.Error = e.Message;
            job.Status = SubAgentStatus.Failed;
        }
        finally
        {
            job.FinishedAt = _clock.GetUtcNow().UtcDateTime;
            sw.Stop();
            var outcome = job.Status.ToString().ToLowerInvariant();
            activity?.SetTag("llmlab.subagent.outcome", outcome);
            LlmLabTelemetry.SubAgentTasks.Add(1,
                new KeyValuePair<string, object?>("agent", profile.Id), new KeyValuePair<string, object?>("outcome", outcome));
            LlmLabTelemetry.SubAgentDuration.Record(sw.Elapsed.TotalSeconds);
            job.Done.TrySetResult();
        }
    }

    private void PruneFinished()
    {
        var finished = _jobs.Values.Where(j => j.Status != SubAgentStatus.Running).OrderBy(j => j.FinishedAt).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - _options.RetainFinished)))
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }
}
