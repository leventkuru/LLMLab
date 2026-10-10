using LlmLab.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace LlmLab.Infrastructure.Mcp;

/// <summary>
/// Bağlı bir MCP sunucusu: istemci ve keşfedilen araçları. Bir çağrı bağlantı hatasıyla düşerse
/// <see cref="Invalidate"/> ile işaretlenir; sonraki turda registry yeniden bağlanır.
/// </summary>
internal sealed class McpServerConnection(McpServerEntry entry, McpClient client, IReadOnlyList<McpClientTool> tools)
{
    public McpServerEntry Entry { get; } = entry;
    public McpClient Client { get; } = client;
    public IReadOnlyList<McpClientTool> Tools { get; } = tools;
    public bool Invalidated { get; private set; }

    public void Invalidate() => Invalidated = true;
}

/// <summary>
/// MCP sunucularına bağlantıları tutar (tekil servis): ilk kullanımda bağlanır, araçları keşfeder ve önbellekler.
/// Bir sunucu kapalıysa tur onsuz devam eder (araçları yoktur ve talimat da bunu yansıtır); yeniden deneme belirli
/// aralıkla yapılır, böylece kapalı bir sunucu her turu bekletmez.
/// </summary>
internal sealed class McpToolRegistry : IAsyncDisposable
{
    private sealed class State(McpServerEntry entry)
    {
        public McpServerEntry Entry { get; } = entry;
        public McpServerConnection? Connection { get; set; }
        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly List<State> _servers;
    private readonly McpOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolRegistry> _log;
    private readonly TimeProvider _clock;

    public McpToolRegistry(
        IOptions<McpOptions> options, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, TimeProvider clock)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<McpToolRegistry>();
        _clock = clock;
        _servers = _options.Servers.Select(s => new State(s)).ToList();
    }

    public TimeSpan CallTimeout => TimeSpan.FromSeconds(_options.CallTimeoutSeconds);

    /// <summary>Bağlanabilen sunucular (belge sunucuları yalnızca <paramref name="includeDocumentServers"/> ise).</summary>
    public async Task<IReadOnlyList<McpServerConnection>> GetConnectionsAsync(bool includeDocumentServers, CancellationToken ct)
    {
        var wanted = _servers.Where(s => includeDocumentServers || !s.Entry.OnlyWithDocuments).ToList();
        var connections = await Task.WhenAll(wanted.Select(s => EnsureConnectedAsync(s, ct)));
        return connections.Where(c => c is not null).Select(c => c!).ToList();
    }

    private async Task<McpServerConnection?> EnsureConnectedAsync(State state, CancellationToken ct)
    {
        if (state.Connection is { Invalidated: false } existing) return existing;
        if (_clock.GetUtcNow() < state.RetryAt) return null;

        await state.Gate.WaitAsync(ct);
        try
        {
            if (state.Connection is { Invalidated: false } raced) return raced;

            if (state.Connection is { } stale)
            {
                state.Connection = null;
                await stale.Client.DisposeAsync();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

            try
            {
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(state.Entry.Url),
                        Name = state.Entry.Name,
                        TransportMode = HttpTransportMode.StreamableHttp,
                    },
                    _httpFactory.CreateClient("mcp"),
                    _loggerFactory,
                    ownsHttpClient: false);

                var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: timeout.Token);
                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

                state.Connection = new McpServerConnection(state.Entry, client, tools.ToList());
                _log.LogInformation("MCP sunucusuna bağlandı: {Server} ({Url}) araçlar: {Tools}",
                    state.Entry.Name, state.Entry.Url, string.Join(", ", tools.Select(t => t.Name)));
                return state.Connection;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                state.RetryAt = _clock.GetUtcNow().AddSeconds(_options.RetryAfterSeconds);
                _log.LogWarning("MCP sunucusuna bağlanılamadı: {Server} ({Url}): {Error}. {Seconds} sn sonra yeniden denenecek.",
                    state.Entry.Name, state.Entry.Url, e.Message, _options.RetryAfterSeconds);
                return null;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Bağlı olmayan sunucuların adları (sağlık/tanılama için).</summary>
    public IReadOnlyList<string> DisconnectedServers() =>
        _servers.Where(s => s.Connection is null or { Invalidated: true }).Select(s => s.Entry.Name).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers)
        {
            if (s.Connection is { } c) await c.Client.DisposeAsync();
        }
    }
}
