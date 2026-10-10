extern alias ToolsServer;

using LlmLab.Infrastructure.Ai;
using LlmLab.Infrastructure.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace LlmLab.Mcp.Tests;

/// <summary>
/// Gerçek MCP protokolü, süreç içinde: araç sunucusu bellek içi (TestServer) çalışır; istemci ve ajan kataloğu ona
/// gerçek MCP istemcisiyle bağlanır. Sunucu kodu ve SDK kullanımı süreç başlatmadan doğrulanır.
/// </summary>
public class McpIntegrationTests : IClassFixture<WebApplicationFactory<ToolsServer::Program>>
{
    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private readonly WebApplicationFactory<ToolsServer::Program> _factory;

    public McpIntegrationTests(WebApplicationFactory<ToolsServer::Program> factory) => _factory = factory;

    private McpToolRegistry Registry(params McpServerEntry[] servers) =>
        new(Options.Create(new McpOptions { Servers = [.. servers], ConnectTimeoutSeconds = 5, RetryAfterSeconds = 60 }),
            new StubHttpClientFactory(_factory.CreateClient()), NullLoggerFactory.Instance, TimeProvider.System);

    private static McpServerEntry Tools(string url = "http://localhost/mcp") => new() { Name = "tools", Url = url };

    [Fact]
    public async Task The_server_publishes_clock_and_calculator_tools_with_descriptions()
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            _factory.CreateClient(), ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "get_current_time" && t.Description.Contains("saat"));
        Assert.Contains(tools, t => t.Name == "calculate" && t.Description.Contains("hesap"));
    }

    [Fact]
    public async Task The_catalog_discovers_tools_and_invokes_them_through_mcp()
    {
        await using var registry = Registry(Tools());
        var catalog = new AgentToolCatalog(registry, NullLogger<AgentToolCatalog>.Instance);

        var set = await catalog.CreateAsync(includeDocumentServers: false, allowedTools: null, new TurnSources(), CancellationToken.None);

        Assert.False(set.CanSearchDocuments);
        Assert.Contains("calculate", set.GeneralToolNames);
        var calculate = set.Tools.OfType<AIFunction>().Single(t => t.Name == "calculate");
        var result = await calculate.InvokeAsync(new AIFunctionArguments { ["expression"] = "2+2*3" });
        Assert.Contains("2+2*3 = 8", result?.ToString());
    }

    [Fact]
    public async Task The_agents_allow_list_hides_every_other_tool()
    {
        await using var registry = Registry(Tools());
        var catalog = new AgentToolCatalog(registry, NullLogger<AgentToolCatalog>.Instance);

        var set = await catalog.CreateAsync(false, ["calculate"], new TurnSources(), CancellationToken.None);

        Assert.Equal(["calculate"], set.Tools.Select(t => t.Name));
        Assert.Equal(["calculate"], set.GeneralToolNames);
    }

    [Fact]
    public async Task An_unreachable_server_yields_no_tools_instead_of_failing_the_turn()
    {
        await using var registry = Registry(Tools("http://localhost:1/yok"));
        var catalog = new AgentToolCatalog(registry, NullLogger<AgentToolCatalog>.Instance);

        var set = await catalog.CreateAsync(includeDocumentServers: true, allowedTools: null, new TurnSources(), CancellationToken.None);

        Assert.Empty(set.Tools);
        Assert.False(set.CanSearchDocuments);
        Assert.Contains("tools", registry.DisconnectedServers());
    }

    [Fact]
    public async Task Document_servers_are_skipped_unless_documents_are_requested()
    {
        // Bu testte "docs" girdisi gerçekte araç sunucusuna işaret eder: yalnızca bayrağın etkisi sınanır.
        await using var registry = Registry(new McpServerEntry { Name = "docs", Url = "http://localhost/mcp", OnlyWithDocuments = true });
        var catalog = new AgentToolCatalog(registry, NullLogger<AgentToolCatalog>.Instance);

        var without = await catalog.CreateAsync(false, null, new TurnSources(), CancellationToken.None);
        var with = await catalog.CreateAsync(true, null, new TurnSources(), CancellationToken.None);

        Assert.Empty(without.Tools);
        Assert.NotEmpty(with.Tools);
    }
}
