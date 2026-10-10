using LlmLab.Application;
using LlmLab.Infrastructure;

// MCP "belgeler" sunucusu: bilgi tabanına (SQL Server vektör + anahtar kelime araması) tek erişim kapısı.
// Ajan API'sinden ayrıdır: RAG eşikleri (MinScore, TopK) ve veritabanı erişimi burada kalır; ajan yalnızca
// "search_documents" aracını çağırır. Aynı sunucu başka bir MCP istemcisine de bilgi tabanı sunabilir.
var builder = WebApplication.CreateBuilder(args);

// Uygulama ve altyapı katmanlarını aynen yeniden kullanırız (aynı arama, aynı veritabanı, aynı embedding).
// Şema/migration'ı bu sunucu yönetmez: onu ajan API'si (agent-api) uygular.
builder.Services
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp("/mcp");

app.Run();
