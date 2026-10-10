using System.Text.Json;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Rag;
using LlmLab.Domain.Conversations;
using LlmLab.Infrastructure.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LlmLab.Infrastructure.Ai;

/// <summary>
/// Bir turda toplanan belge kaynakları (birikimli, 1'den numaralı). Aynı kaynak turda birden çok aramada gelse de
/// numarası ilk görüldüğü sıradır; böylece cevaptaki [n] atıfları arayüzdeki kaynak listesiyle eşleşir.
/// </summary>
internal sealed class TurnSources
{
    private readonly List<SourceReference> _collected = [];
    private readonly object _gate = new();

    public bool Searched { get; private set; }

    public IReadOnlyList<SourceReference> Snapshot()
    {
        lock (_gate) return _collected.ToList();
    }

    public int Count
    {
        get { lock (_gate) return _collected.Count; }
    }

    public IReadOnlyList<(int Number, SourceReference Source)> Add(IEnumerable<SourceReference> hits)
    {
        lock (_gate)
        {
            Searched = true;
            var numbered = new List<(int, SourceReference)>();
            foreach (var hit in hits)
            {
                var index = _collected.FindIndex(c => c.ChunkId == hit.ChunkId);
                if (index < 0)
                {
                    _collected.Add(hit);
                    index = _collected.Count - 1;
                }
                numbered.Add((index + 1, _collected[index]));
            }
            return numbered;
        }
    }
}

/// <summary>Bir tur için ajana verilecek araçlar ve talimatı kurmak için gereken bilgi.</summary>
internal sealed record ToolSet(IReadOnlyList<AITool> Tools, bool CanSearchDocuments, IReadOnlyList<string> GeneralToolNames);

/// <summary>
/// Ajana verilen tüm araçların tek kaynağı. Araçlar MCP sunucularından keşfedilir; burada yalnızca ajanın o tura
/// özgü ihtiyaçları için ince bir sarmalayıcıdan geçirilir:
///  - hata yönetimi: sunucu düşerse tur çökmez, model "araç çalışmadı" sonucunu görür (ve uydurmamayı söyler);
///  - `search_documents`: sonuç yapısal gelir; kaynaklar tura göre numaralanır, arayüze kaynak listesi olarak bildirilir
///    ve modele etiketli VERİ olarak (prompt injection savunması) sunulur.
/// Ajan kayıt defteri (faz 2, madde 8) bir ajana yalnızca "şu adlı araçlar" demekle yetinecek; bu katalog çözer.
/// </summary>
internal sealed class AgentToolCatalog(McpToolRegistry registry, ILogger<AgentToolCatalog> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="allowedTools">Ajanın kayıt defterindeki izinli araç adları. Boş = verilen tüm araçlar.</param>
    public async Task<ToolSet> CreateAsync(
        bool includeDocumentServers, IReadOnlyList<string>? allowedTools, TurnSources turn, CancellationToken ct)
    {
        var allowed = allowedTools is { Count: > 0 } ? new HashSet<string>(allowedTools, StringComparer.Ordinal) : null;
        var connections = await registry.GetConnectionsAsync(includeDocumentServers, ct);

        var tools = new List<AITool>();
        var general = new List<string>();
        var canSearch = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in connections)
        {
            foreach (var tool in server.Tools)
            {
                // Belge araçları yalnızca "Belgeler" açıkken verilir (sunucu yanlışlıkla başka yerde yayınlasa bile).
                var isDocumentTool = tool.Name is DocumentToolContract.SearchToolName or DocumentToolContract.ListToolName;
                if (isDocumentTool && !includeDocumentServers) continue;

                // Ajanın izin listesi (kayıt defteri): listede olmayan araç bu ajana hiç gösterilmez.
                if (allowed is not null && !allowed.Contains(tool.Name)) continue;

                if (!seen.Add(tool.Name))
                {
                    log.LogWarning("Aynı ada sahip ikinci araç yok sayıldı: {Tool} ({Server})", tool.Name, server.Entry.Name);
                    continue;
                }

                if (tool.Name == DocumentToolContract.SearchToolName) canSearch = true;
                else general.Add(tool.Name);

                tools.Add(new McpFunction(tool, (args, token) => CallAsync(server, tool, args, turn, token)));
            }
        }

        return new ToolSet(tools, canSearch, general);
    }

    private async ValueTask<object?> CallAsync(
        McpServerConnection server, McpClientTool tool, AIFunctionArguments args, TurnSources turn, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(registry.CallTimeout);

        try
        {
            var arguments = args.ToDictionary(kv => kv.Key, kv => kv.Value);
            var result = await server.Client.CallToolAsync(tool.Name, arguments, cancellationToken: timeout.Token);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

            if (result.IsError == true)
            {
                log.LogWarning("MCP aracı hata döndürdü: {Tool}: {Text}", tool.Name, text);
                return $"Araç hata verdi ({tool.Name}): {text}";
            }

            if (tool.Name == DocumentToolContract.SearchToolName)
            {
                var parsed = ParseSearchResult(result, text);
                if (parsed is not null)
                {
                    return AgentPrompts.FormatSearchResult(turn.Add(parsed.Hits));
                }
            }

            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // kullanıcı durdurdu: hata değil
        }
        catch (Exception e)
        {
            // Bağlantı ya da zaman aşımı: tur çökmesin, model sonucu uydurmasın. Bağlantı bir sonraki turda yenilenir.
            log.LogWarning(e, "MCP araç çağrısı başarısız: {Tool} ({Server})", tool.Name, server.Entry.Name);
            server.Invalidate();
            return $"Araç şu an çalışmıyor ({tool.Name}). Sonucu tahmin etme ya da uydurma; kullanıcıya aracın şu an " +
                   "kullanılamadığını söyle.";
        }
    }

    private static DocumentSearchResult? ParseSearchResult(CallToolResult result, string text)
    {
        try
        {
            if (result.StructuredContent is { } structured)
            {
                return JsonSerializer.Deserialize<DocumentSearchResult>(structured.ToString(), Json);
            }
            // Yapısal içerik yoksa SDK metin bloğunda aynı JSON'u taşır.
            return JsonSerializer.Deserialize<DocumentSearchResult>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>MCP aracının AIFunction görünümü: adı, açıklaması ve şeması aynı kalır, çağrı katalog tarafından yönetilir.</summary>
internal sealed class McpFunction(
    McpClientTool inner, Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invoke)
    : DelegatingAIFunction(inner)
{
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        invoke(arguments, cancellationToken);
}
