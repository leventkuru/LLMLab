using System.Runtime.CompilerServices;
using System.Text.Json;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using LlmLab.Application.Rag;
using LlmLab.Application.Telemetry;
using LlmLab.Infrastructure.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

// Her iki kütüphanede de ChatRole var; modele giden mesajlar için Microsoft.Extensions.AI'ınkini kullan.
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LlmLab.Infrastructure.Ai;

/// <summary>
/// IChatAgent'ın Microsoft Agent Framework (ChatClientAgent) üzerindeki uygulaması.
///
/// Eski sürümden temel farklar:
///  - Oturum: AgentSession ajanın çalışma belleğidir (geçmiş mesajlar, araç çağrıları). Tur sonunda serileştirilip
///    sohbetle birlikte saklanır, sonraki turda geri yüklenir; geçmişi elle kurmak gerekmez.
///  - Araçlar: ajan hiçbir aracı kendisi barındırmaz. Araçlar MCP sunucularından keşfedilir (saat, hesap, belge
///    araması, belge listesi) ve ne zaman çağıracağına model karar verir ("agentic RAG": selamlaşmada aramaz, belge
///    sorusunda arar). Ajanın kodu araçlardan habersizdir; yeni bir MCP sunucusu eklemek yapılandırma işidir.
///  - Akış: ajanın güncellemeleri (düşünce, metin, araç çağrısı, araç sonucu) tek bir akıştan gelir.
/// </summary>
internal sealed class MafChatAgent : ILocalChatAgent
{
    private readonly IChatClient _chatClient;
    private readonly AgentToolCatalog _catalog;
    private readonly SubAgentTools _subAgentTools;
    private readonly AgentSettings _settings;
    private readonly TelemetryOptions _telemetry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MafChatAgent> _log;

    public MafChatAgent(
        IChatClient chatClient, AgentToolCatalog catalog, SubAgentTools subAgentTools, IOptions<AgentSettings> settings,
        IOptions<TelemetryOptions> telemetry, ILoggerFactory loggerFactory)
    {
        _chatClient = chatClient;
        _catalog = catalog;
        _subAgentTools = subAgentTools;
        _settings = settings.Value;
        _telemetry = telemetry.Value;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<MafChatAgent>();
    }

    // Ajan her turda yeniden kurulur: talimat o turun GERÇEK yeteneklerine (hangi MCP sunucuları ayakta, "Belgeler"
    // açık mı) göre derlenir. Kurmak ucuzdur (ağ işi yok); oturum geçmişi ajana değil AgentSession'a aittir.
    private AIAgent CreateAgent(string instructions) =>
        new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "LlmLabAssistant",
                Description = "MCP araçlarını kullanan, belgelerde arama yapabilen Türkçe sohbet asistanı.",
                ChatOptions = new ChatOptions { Instructions = instructions },

                // Oturum geçmişi bellekte tutulur ve serileştirilir. Reducer, geçmişi son N mesajla sınırlar:
                // modelin bağlam penceresi sonsuz değildir ve araç sonuçları (kaynak metinleri) geçmişi hızla şişirir.
#pragma warning disable MEAI001 // MessageCountingChatReducer Microsoft'ta "deneysel" işaretli; API değişirse burası güncellenir.
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MessageCountingChatReducer(_settings.MaxHistoryMessages),
                }),
#pragma warning restore MEAI001
            },
            _loggerFactory)
        // Gözlemlenebilirlik: ajanın her çalışması bir iz aralığıdır (invoke_agent); içindeki model çağrıları ve araç
        // çalıştırmaları alt aralıklar olarak görünür. Mesaj/araç içeriği yalnızca CaptureSensitiveData açıksa yazılır.
        .AsBuilder()
        .UseOpenTelemetry(LlmLabTelemetry.AgentSourceName, agent => agent.EnableSensitiveData = _telemetry.CaptureSensitiveData)
        .Build();

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // 1) Bu tura verilecek araçlar ve onlara göre derlenen talimat. Ajanın kimliği kayıt defterinden gelir:
        //    rol metni, izinli araçlar ve (koordinatörse) alt ajan araçları.
        var profile = request.Profile ?? AgentProfile.Default;
        var turn = new TurnSources();
        var toolSet = await _catalog.CreateAsync(request.UseDocuments, profile.AllowedTools, turn, ct);

        var tools = new List<AITool>(toolSet.Tools);
        var generalNames = new List<string>(toolSet.GeneralToolNames);
        var subAgents = new SubAgentTurn();
        if (profile.CanDelegate)
        {
            tools.AddRange(_subAgentTools.Create(subAgents));
            generalNames.AddRange(SubAgentTools.ToolNames);
        }

        // "Belgeler" açık olsa da ajanın izin listesinde belge araması yoksa "arama hizmeti kapalı" demek yanlış olur:
        // o durumda belge erişimi hiç istenmemiş gibi davranılır.
        var searchAllowed = profile.AllowedTools.Count == 0 || profile.AllowedTools.Contains(AgentPrompts.SearchToolName);
        var agent = CreateAgent(AgentPrompts.Compose(
            new AgentCapabilities(
                request.UseDocuments && searchAllowed, toolSet.CanSearchDocuments, generalNames,
                profile.Instructions, request.ExtraInstructions, profile.CanDelegate)));

        // Her turun ayarı günlüğe yazılır: "neden araç çağrılmadı?" sorusunun ilk cevabı genellikle burada yatar.
        _log.LogInformation(
            "Ajan turu: Ajan={Agent} UseDocuments={UseDocuments} Think={Think} CanSearch={CanSearch} Araçlar=[{Tools}]",
            profile.Id, request.UseDocuments, request.Think, toolSet.CanSearchDocuments,
            string.Join(", ", tools.Select(t => t.Name)));

        // 2) Oturumu geri yükle (ya da yeni başlat).
        var session = string.IsNullOrEmpty(request.SessionState)
            ? await agent.CreateSessionAsync(ct)
            : await agent.DeserializeSessionAsync(JsonDocument.Parse(request.SessionState).RootElement, cancellationToken: ct);

        // 3) Bu tura özel seçenekler: düşünme, bağlam boyutu ve araçlar.
        var chatOptions = new ChatOptions
        {
            Tools = tools,
            // Düşünme akışını cevaptan ayrı ver (Ollama "thinking" alanı) ve bağlamı büyüt.
            RawRepresentationFactory = _ => new ChatRequest
            {
                Think = request.Think,
                Options = new RequestOptions { NumCtx = _settings.ContextTokens },
            },
        };

        // Devreden ajanda kullanıcının mesajına kısa bir "önce devretmeyi düşün" yönlendirmesi eklenir (neden:
        // AgentPrompts.DelegationReminder). Arayüzdeki transkript asıl mesajı tutar; yönlendirme yalnızca modele gider.
        var userText = profile.CanDelegate ? request.Message + AgentPrompts.DelegationReminder : request.Message;
        var input = new List<ChatMessage> { new(ChatRole.User, userText) };

        // 4) Çalıştır ve olayları dışarı ver.
        //
        // Koordinatörde cevap metni tamponlanır (düşünme ve araç satırları yine canlı akar). Neden: küçük model alt
        // ajan görevlerini başlatıp SONUÇLARINI ALMADAN cevap yazabiliyor ve sonucu uyduruyor (gözlendi). Tur bitiminde
        // sonucu alınmamış görev varsa o metin ATILIR, sonuçlar sistem tarafından verilir ve model yeniden cevaplar.
        var bufferText = profile.CanDelegate;
        const int maxContinuations = 2;
        var emittedSources = -1; // son bildirilen kaynak sayısı (-1 = hiç bildirilmedi)

        for (var pass = 0; ; pass++)
        {
            var buffered = new System.Text.StringBuilder();

            await foreach (var update in agent.RunStreamingAsync(input, session, new ChatClientAgentRunOptions(chatOptions), ct))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextReasoningContent r when !string.IsNullOrEmpty(r.Text):
                            yield return new ThinkingDelta(r.Text);
                            break;

                        case TextContent t when !string.IsNullOrEmpty(t.Text):
                            if (bufferText) buffered.Append(t.Text);
                            else yield return new TextDelta(t.Text);
                            break;

                        case FunctionCallContent call:
                            _log.LogInformation("Ajan araç çağırdı: {Tool}", call.Name);
                            yield return new ToolInvoked(call.Name, FormatArguments(call.Arguments));
                            break;

                        case FunctionResultContent when turn.Searched && turn.Count != emittedSources:
                            // Belge araması sonucu geldi: arayüz kaynak listesini göstersin. Boş liste = arandı ama bulunamadı.
                            emittedSources = turn.Count;
                            yield return new SourcesFound(turn.Snapshot());
                            break;
                    }
                }
            }

            var pending = profile.CanDelegate ? subAgents.Pending() : [];
            if (pending.Count == 0)
            {
                if (buffered.Length > 0) yield return new TextDelta(buffered.ToString());
                break;
            }

            if (pass >= maxContinuations)
            {
                // Vazgeç: metni ver ama sonuçların alınmadığını açıkça söyle (sessizce uydurma bırakma).
                if (buffered.Length > 0) yield return new TextDelta(buffered.ToString());
                yield return new TextDelta(
                    $"\n\n> Not: şu alt ajan görevlerinin sonuçları alınamadığı için bu cevap onlara dayanmıyor olabilir: {string.Join(", ", pending)}.");
                break;
            }

            // Güvence: bekleyen görevlerin sonuçlarını sistem alır, modele "sistem notu" olarak verir. Arayüzde
            // bu şeffaf görünsün diye araç çağrısı olarak da bildirilir.
            _log.LogWarning("Koordinatör {Count} alt ajan görevinin sonucunu almadan cevap verdi; sonuçlar sistem tarafından veriliyor.", pending.Count);
            foreach (var id in pending)
                yield return new ToolInvoked(SubAgentTools.GetName, $"taskId={id} (sistem aldı)");

            var (message, _) = await _subAgentTools.CollectAsync(pending, TimeSpan.FromSeconds(180), ct);
            input = [new ChatMessage(ChatRole.User, message)];
        }
        // 5) Oturumu serileştir: sonraki tur kaldığı yerden devam eder.
        var state = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        yield return new SessionSaved(state.GetRawText());
    }

    private static string? FormatArguments(IDictionary<string, object?>? arguments) =>
        arguments is { Count: > 0 }
            ? string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))
            : null;
}
