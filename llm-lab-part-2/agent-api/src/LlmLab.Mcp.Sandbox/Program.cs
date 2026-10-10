using LlmLab.Mcp.Sandbox;

// MCP "sandbox" sunucusu: ajanın yazdığı kodu İZİN VERİLEN SINIRLAR içinde çalıştırır.
// Her çalıştırma tek kullanımlık bir Docker konteyneridir: ağ yok, salt okunur dosya sistemi, bellek/CPU/süre/süreç
// sınırı, kök olmayan kullanıcı, tüm yetenekler (capabilities) kapalı. Konteyner bittiğinde (ya da zaman aşımında
// öldürüldüğünde) silinir; çalıştırmalar arasında durum kalmaz.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SandboxOptions>().Bind(builder.Configuration.GetSection(SandboxOptions.SectionName));
builder.Services.AddSingleton<DockerSandbox>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapMcp("/mcp");

app.Run();
