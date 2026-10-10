using System.Runtime.CompilerServices;
using System.Text.Json;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using LlmLab.Application.Rag;
using LlmLab.Application.Telemetry;
using LlmLab.Domain.Conversations;
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
///  - Araç: belge araması her mesajda zorla yapılmaz; ajana `search_documents` aracı verilir ve ne zaman
///    çağıracağına model karar verir ("agentic RAG"). Selamlaşmada çağırmaz, belge sorusunda çağırır.
///  - Akış: ajanın güncellemeleri (düşünce, metin, araç çağrısı, araç sonucu) tek bir akıştan gelir.
/// </summary>
internal sealed class MafChatAgent : IChatAgent
{
    // İki ajan: "Belgeler" açıkken arama aracını bilen, kapalıyken araç olmadığını söyleyen talimatla. Neden ayrı?
    // Ajanın gerçekte aracı yokken talimat "sana arama aracı verilmiş olabilir" derse model olmayan bir araçtan
    // bahsedip "belgeleri kontrol ediyorum [1]" yazıyor ve cevabı uyduruyor. Oturum biçimi (InMemoryChatHistoryProvider)
    // ikisinde de aynıdır; bir sohbetin turları arasında Belgeler açılıp kapanabilir.
    private readonly AIAgent _docsAgent;
    private readonly AIAgent _plainAgent;
    private readonly IDocumentSearch _search;
    private readonly RagOptions _rag;
    private readonly AgentSettings _settings;
    private readonly ILogger<MafChatAgent> _log;

    public MafChatAgent(
        IChatClient chatClient, IDocumentSearch search, IOptions<RagOptions> rag, IOptions<AgentSettings> settings,
        IOptions<TelemetryOptions> telemetry, ILoggerFactory loggerFactory)
    {
        _search = search;
        _rag = rag.Value;
        _settings = settings.Value;
        _log = loggerFactory.CreateLogger<MafChatAgent>();

        _docsAgent = CreateAgent(chatClient, AgentPrompts.Instructions, telemetry.Value, loggerFactory);
        _plainAgent = CreateAgent(chatClient, AgentPrompts.InstructionsWithoutDocuments, telemetry.Value, loggerFactory);
    }

    private AIAgent CreateAgent(IChatClient chatClient, string instructions, TelemetryOptions telemetry, ILoggerFactory loggerFactory) =>
        new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "LlmLabAssistant",
                Description = "Belgelerde arama yapabilen Türkçe sohbet asistanı.",
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
            loggerFactory)
        // Gözlemlenebilirlik: ajanın her çalışması bir iz aralığıdır (invoke_agent); içindeki model çağrıları ve araç
        // çalıştırmaları alt aralıklar olarak görünür. Mesaj/araç içeriği yalnızca CaptureSensitiveData açıksa yazılır.
        .AsBuilder()
        .UseOpenTelemetry(LlmLabTelemetry.AgentSourceName, agent => agent.EnableSensitiveData = telemetry.CaptureSensitiveData)
        .Build();

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Belgeler açık/kapalıya göre doğru talimatlı ajan. Her turun ayarı günlüğe yazılır: "neden araç çağrılmadı?"
        // sorusunun ilk cevabı genellikle burada (UseDocuments=False) yatar.
        var agent = request.UseDocuments ? _docsAgent : _plainAgent;
        _log.LogInformation("Ajan turu: UseDocuments={UseDocuments} Think={Think} SearchFirst={SearchFirst}",
            request.UseDocuments, request.Think, _settings.SearchFirst);

        // 1) Oturumu geri yükle (ya da yeni başlat).
        var session = string.IsNullOrEmpty(request.SessionState)
            ? await agent.CreateSessionAsync(ct)
            : await agent.DeserializeSessionAsync(JsonDocument.Parse(request.SessionState).RootElement, cancellationToken: ct);

        // 2) Bu tura özel seçenekler: düşünme, bağlam boyutu ve (isteğe bağlı) belge aracı.
        var collected = new List<SourceReference>(); // bu turda ajana verilen kaynaklar (birikimli, numaralı)
        var searched = false;

        // Hem modelin çağırdığı araç hem de "aramayı garantile" ön aramasının kullandığı tek arama işlevi.
        async Task<string> SearchDocuments(string query, CancellationToken token)
        {
            searched = true;
            var hits = await _search.SearchAsync(query, _rag.ChatTopK, token);
            var relevant = hits.Where(h => _rag.IsRelevant(h.Score, h.KeywordHits)).ToList();

            // Aynı kaynak turda birden çok aramada gelebilir: numarası ilk görüldüğü sıradır.
            var numbered = new List<(int Number, SourceReference Source)>();
            foreach (var hit in relevant)
            {
                var index = collected.FindIndex(c => c.ChunkId == hit.ChunkId);
                if (index < 0)
                {
                    collected.Add(hit);
                    index = collected.Count - 1;
                }
                numbered.Add((index + 1, collected[index]));
            }
            return AgentPrompts.FormatSearchResult(numbered);
        }

        var tools = new List<AITool>();
        if (request.UseDocuments)
        {
            tools.Add(AIFunctionFactory.Create(SearchDocuments, AgentPrompts.SearchToolName, AgentPrompts.SearchToolDescription));
        }

        // Girdi mesajları: kullanıcı mesajı ve, aramayı garantile açıksa, ön aramanın "sanki ajan çağırmış" izi.
        var input = new List<ChatMessage> { new(ChatRole.User, request.Message) };
        var preflight = request.UseDocuments && _settings.SearchFirst;
        if (preflight)
        {
            // Ölçüm: düşünme açıkken qwen3:8b aracı sık atlıyor ("arama yapacağım" yazıp turu bitiriyor) ve cevabı
            // uyduruyor. Çözüm modele güvenmemek: ilk aramayı biz yaparız, sonucu araç çağrısı + sonucu olarak
            // konuşmaya koyarız; model aynı bağlamda cevaplar (gerekirse yine arar).
            var callId = Guid.NewGuid().ToString("N");
            var result = await SearchDocuments(request.Message, ct);
            input.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, AgentPrompts.SearchToolName, new Dictionary<string, object?> { ["query"] = request.Message })]));
            input.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]));
        }

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

        // 3) Çalıştır ve olayları dışarı ver.
        var emittedSources = -1; // son bildirilen kaynak sayısı (-1 = hiç bildirilmedi)
        if (preflight)
        {
            // Ön arama da arayüzde ve transkriptte görünür: kullanıcı "ajan ne yaptı?"yı her zaman görür.
            yield return new ToolInvoked(AgentPrompts.SearchToolName, $"query={request.Message}");
            emittedSources = collected.Count;
            yield return new SourcesFound(collected.ToList());
        }

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
                        yield return new TextDelta(t.Text);
                        break;

                    case FunctionCallContent call:
                        _log.LogInformation("Ajan araç çağırdı: {Tool}", call.Name);
                        yield return new ToolInvoked(call.Name, FormatArguments(call.Arguments));
                        break;

                    case FunctionResultContent when searched && collected.Count != emittedSources:
                        // Araç sonucu geldi: arayüz kaynak listesini göstersin. Boş liste = arandı ama bulunamadı.
                        emittedSources = collected.Count;
                        yield return new SourcesFound(collected.ToList());
                        break;
                }
            }
        }

        // 4) Oturumu serileştir: sonraki tur kaldığı yerden devam eder.
        var state = await agent.SerializeSessionAsync(session, cancellationToken: ct);
        yield return new SessionSaved(state.GetRawText());
    }

    private static string? FormatArguments(IDictionary<string, object?>? arguments) =>
        arguments is { Count: > 0 }
            ? string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))
            : null;
}
