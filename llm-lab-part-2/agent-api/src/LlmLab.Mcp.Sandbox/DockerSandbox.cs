using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace LlmLab.Mcp.Sandbox;

/// <summary>Sandbox ayarları ("Sandbox" bölümü). Sınırlar kodda değil yapılandırmadadır; model bunları değiştiremez.</summary>
public sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    public string DockerBinary { get; set; } = "docker";

    /// <summary>Kodun çalıştığı imaj. Önceden yerelde bulunmalıdır (docker pull python:3.12-slim); sunucu imaj indirmez.</summary>
    public string Image { get; set; } = "python:3.12-slim";

    public int DefaultTimeoutSeconds { get; set; } = 10;
    public int MaxTimeoutSeconds { get; set; } = 60;
    public int MemoryMb { get; set; } = 256;
    public double Cpus { get; set; } = 1.0;
    public int PidsLimit { get; set; } = 64;
    public int TmpfsMb { get; set; } = 16;
    public int MaxOutputChars { get; set; } = 8000;
    public int MaxCodeChars { get; set; } = 20000;

    /// <summary>Aynı anda çalışabilecek en fazla konteyner sayısı.</summary>
    public int MaxConcurrent { get; set; } = 2;
}

public sealed record SandboxResult(
    int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Truncated, double Seconds, string? Failure = null)
{
    /// <summary>Modele verilen metin: kısa, ayrıştırması kolay.</summary>
    public string ToText()
    {
        if (Failure is not null) return $"Kod çalıştırılamadı: {Failure}";

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"exit_code: {ExitCode}\n");
        sb.Append(CultureInfo.InvariantCulture, $"süre: {Seconds:0.0} sn\n");
        if (TimedOut) sb.Append("ZAMAN AŞIMI: kod süre sınırını aştığı için öldürüldü.\n");
        if (Truncated) sb.Append("NOT: çıktı uzunluk sınırı nedeniyle kısaltıldı.\n");
        sb.Append("--- stdout ---\n").Append(Stdout.Length == 0 ? "(boş)" : Stdout.TrimEnd()).Append('\n');
        if (Stderr.Length > 0) sb.Append("--- stderr ---\n").Append(Stderr.TrimEnd()).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// Kodu tek kullanımlık, sınırlı bir Docker konteynerinde çalıştırır. Kod komut satırına değil STDIN'e verilir
/// (tırnak/kaçış sorunu ve komut enjeksiyonu yok) ve ProcessStartInfo.ArgumentList kullanılır (kabuk yok).
/// </summary>
public sealed class DockerSandbox(IOptions<SandboxOptions> options, ILogger<DockerSandbox> log)
{
    private readonly SandboxOptions _o = options.Value;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.Value.MaxConcurrent));

    /// <summary>docker run argümanları. Saf bir işlevdir; sınırlar burada tek yerde ve test edilebilir.</summary>
    public static IReadOnlyList<string> BuildArguments(SandboxOptions o, string containerName) =>
    [
        "run", "--rm", "-i",
        "--name", containerName,
        "--pull", "never",                       // sessizce imaj indirme
        "--network", "none",                      // ağ yok
        "--read-only",                            // salt okunur kök dosya sistemi
        "--tmpfs", $"/tmp:rw,noexec,nosuid,size={o.TmpfsMb}m", // tek yazılabilir yer, bellekte, çalıştırılamaz
        "--memory", $"{o.MemoryMb}m", "--memory-swap", $"{o.MemoryMb}m",
        "--cpus", o.Cpus.ToString("0.##", CultureInfo.InvariantCulture),
        "--pids-limit", o.PidsLimit.ToString(CultureInfo.InvariantCulture),
        "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges",
        "--user", "65534:65534",                  // nobody; kök değil
        "-w", "/tmp",
        "-e", "HOME=/tmp", "-e", "PYTHONDONTWRITEBYTECODE=1", "-e", "PYTHONUNBUFFERED=1",
        o.Image,
        "python", "-I", "-",                      // -I: izole mod (ortam değişkeni ve kullanıcı site paketleri yok); program stdin'den
    ];

    public async Task<SandboxResult> RunPythonAsync(string code, int timeoutSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return Failed("Kod boş.");
        if (code.Length > _o.MaxCodeChars) return Failed($"Kod en fazla {_o.MaxCodeChars} karakter olabilir.");

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? _o.DefaultTimeoutSeconds : timeoutSeconds, 1, _o.MaxTimeoutSeconds));
        var name = "llmlab-sbx-" + Guid.NewGuid().ToString("N")[..12];

        await _slots.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(_o.DockerBinary)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in BuildArguments(_o, name)) psi.ArgumentList.Add(a);

            var sw = Stopwatch.StartNew();
            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception e)
            {
                log.LogError(e, "docker başlatılamadı");
                return Failed($"docker çalıştırılamadı ({e.Message}). Docker Desktop açık mı?");
            }

            var stdout = new CappedReader(process.StandardOutput, _o.MaxOutputChars);
            var stderr = new CappedReader(process.StandardError, _o.MaxOutputChars);
            var readOut = stdout.RunAsync();
            var readErr = stderr.RunAsync();

            try
            {
                await process.StandardInput.WriteAsync(code.AsMemory(), ct);
                process.StandardInput.Close();
            }
            catch (IOException) { /* kod çok erken bitti; çıktıyı yine de oku */ }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timer.Token);
            }
            catch (OperationCanceledException)
            {
                // Zaman aşımı ya da istek iptali: konteyneri öldür (docker istemci sürecini öldürmek konteyneri durdurmaz).
                timedOut = !ct.IsCancellationRequested;
                await KillContainerAsync(name);
                try { process.Kill(entireProcessTree: true); } catch { /* zaten bitti */ }
                if (ct.IsCancellationRequested) throw;
            }

            await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            sw.Stop();

            var exit = process.HasExited ? process.ExitCode : -1;
            var errText = stderr.Text;

            // docker'ın kendi hataları: imaj yok (--pull never), daemon kapalı.
            if (exit is 125 or 126 or 127 && !timedOut)
            {
                return Failed(DescribeDockerFailure(errText));
            }

            return new SandboxResult(exit, stdout.Text, errText, timedOut, stdout.Truncated || stderr.Truncated, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            _slots.Release();
        }
    }

    private string DescribeDockerFailure(string stderr)
    {
        if (stderr.Contains("No such image", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Unable to find image", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("pull access denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Sandbox imajı ('{_o.Image}') yerelde yok. Yöneticinin şunu çalıştırması gerekir: docker pull {_o.Image}";
        }
        return $"docker hatası: {stderr.Trim()}";
    }

    private async Task KillContainerAsync(string name)
    {
        try
        {
            var psi = new ProcessStartInfo(_o.DockerBinary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "kill", name }) psi.ArgumentList.Add(a);
            using var kill = Process.Start(psi);
            if (kill is not null) await kill.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Konteyner öldürülemedi: {Name}", name);
        }
    }

    private static SandboxResult Failed(string reason) => new(-1, "", "", false, false, 0, reason);

    /// <summary>Akışı en fazla <c>max</c> karakter tutar; fazlasını okuyup atar (süreç yazarken tıkanmasın diye).</summary>
    private sealed class CappedReader(StreamReader reader, int max)
    {
        private readonly StringBuilder _sb = new();
        public bool Truncated { get; private set; }
        public string Text => _sb.ToString();

        public async Task RunAsync()
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var room = max - _sb.Length;
                if (room <= 0) { Truncated = true; continue; }
                if (read > room) { _sb.Append(buffer, 0, room); Truncated = true; }
                else _sb.Append(buffer, 0, read);
            }
        }
    }
}

[McpServerToolType]
public static class SandboxTools
{
    [McpServerTool(Name = "run_python", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description(
        "Python kodunu izole, tek kullanımlık bir ortamda çalıştırır ve stdout/stderr/çıkış kodunu döndürür. " +
        "Ağ erişimi YOK, dosya sistemi salt okunur (yalnızca /tmp yazılabilir ve çalıştırmalar arasında kalmaz), " +
        "bellek ve süre sınırlıdır; yalnızca standart kütüphane vardır (numpy, pandas vb. YOK). " +
        "Sonucu görmek için print() kullan. Hesap, veri işleme, algoritma doğrulama ve küçük denemeler için kullan. " +
        "ÖNEMLİ: kodun çıktısını asla kendin yazma; sonucu yalnızca bu aracın döndürdüğü stdout'tan al. " +
        "Kütüphane (ör. numpy) yoksa standart kütüphaneyle (random, statistics, math) yeniden dene.")]
    public static async Task<string> RunPython(
        DockerSandbox sandbox,
        [Description("Çalıştırılacak tam Python programı (tek dosya). Sonuçları print() ile yazdır.")] string code,
        [Description("İsteğe bağlı süre sınırı (saniye); varsayılan 10, en fazla 60.")] int timeoutSeconds = 0,
        CancellationToken ct = default)
    {
        var result = await sandbox.RunPythonAsync(code, timeoutSeconds, ct);
        return result.ToText();
    }
}
