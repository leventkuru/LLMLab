using LlmLab.Application.Abstractions;

namespace LlmLab.Infrastructure.Mcp;

/// <summary>
/// Ajan kayıt defteri düzenleyicisinin "hangi araçları seçebilirim?" listesi: ayakta olan MCP sunucularından keşfedilen
/// araçlar. Kapalı bir sunucunun araçları listede görünmez (yeniden bağlanma denemesi registry'dedir).
/// </summary>
internal sealed class McpToolInventory(McpToolRegistry registry) : IToolInventory
{
    public async Task<IReadOnlyList<ToolInfo>> ListAsync(CancellationToken ct)
    {
        var connections = await registry.GetConnectionsAsync(includeDocumentServers: true, ct);
        return connections
            .SelectMany(c => c.Tools.Select(t => new ToolInfo(t.Name, t.Description ?? "", c.Entry.Name, c.Entry.OnlyWithDocuments)))
            .OrderBy(t => t.Server).ThenBy(t => t.Name)
            .ToList();
    }
}
