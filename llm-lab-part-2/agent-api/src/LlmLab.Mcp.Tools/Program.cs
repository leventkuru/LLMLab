using LlmLab.Mcp.Tools;

// MCP "araçlar" sunucusu: ajandan bağımsız, durumsuz ve yan etkisiz küçük araçlar (saat, hesap).
// Neden ayrı süreç? Aynı araçlar herhangi bir MCP istemcisinden (bu API'nin ajanı, Claude Desktop, başka bir ajan)
// kullanılabilir; ajan koduna dokunmadan eklenip çıkarılır, ayrı ölçeklenir ve ayrı yetkilendirilir.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<ToolsOptions>().Bind(builder.Configuration.GetSection(ToolsOptions.SectionName));

builder.Services
    .AddMcpServer()
    // Durumsuz mod: oturum tutulmaz, her istek bağımsızdır. Sunucudan istemciye istek (sampling, elicitation)
    // gerekmediği sürece önerilen moddur ve çok kopyalı çalıştırmayı kolaylaştırır.
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp("/mcp");

app.Run();

public partial class Program; // testler için
