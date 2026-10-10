using A2A;
using A2A.AspNetCore;
using LlmLab.AgentHost;
using LlmLab.Application;
using LlmLab.Infrastructure;
using LlmLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// A2A servisi: kayıt defterindeki ajanları Agent-to-Agent (A2A) protokolüyle AYRI bir servis olarak sunar.
// Ana API (agent-api), Hosting=Remote olan bir ajanı çalıştırırken bu servisin /a2a/{ajan} uç noktasına gider;
// başka A2A uyumlu istemciler de aynı uç noktaları kullanabilir. Ajanın talimatı, izinli araçları ve MCP erişimi
// yerel çalışmayla aynıdır (aynı çalışma zamanı); fark yalnızca sürecin ayrı olmasıdır (ölçek, izolasyon, sahiplik).
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration);

var publicBase = (builder.Configuration["A2AHost:PublicBaseUrl"] ?? "http://localhost:5201").TrimEnd('/');

// A2A sunucuları servis kaydı sırasında (Build'den ÖNCE) kaydedilmek zorunda; bu yüzden ajan listesi, DI kurulmadan,
// doğrudan veritabanından okunur. Veritabanı henüz hazır değilse (API ilk kez migration yapmamışsa) servis uyarı verip
// boş açılır; hazır olunca yeniden başlatın. Yeni bir ajan eklemek de servisin yeniden başlatılmasını ister.
var published = new List<(string Id, string Name, string Description)>();
try
{
    var connection = builder.Configuration.GetConnectionString(LlmLab.Infrastructure.DependencyInjection.ConnectionStringName)
        ?? throw new InvalidOperationException("Bağlantı dizesi tanımlı değil.");
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
    await using var db = new AppDbContext(options);

    // Koordinatör yalnızca iç kullanım içindir: alt ajan araçları (süreç içi görevler) bu serviste çalışmaz.
    var agents = await db.Agents.AsNoTracking().Where(a => a.Enabled && !a.CanDelegate).OrderBy(a => a.Id).ToListAsync();
    published.AddRange(agents.Select(a => (a.Id, a.DisplayName, a.Description)));
}
catch (Exception e)
{
    Console.WriteLine($"UYARI: ajan kayıt defteri okunamadı ({e.GetType().Name}: {e.Message}). " +
                      "Önce agent-api'yi çalıştırın, sonra bu servisi yeniden başlatın.");
}

// Ajanlar kurulumda oluşturulur ama DI kapsamlarına Build'den sonra erişir: erişimi tutucu sağlar.
var holder = new ScopeFactoryHolder();
var hosted = published
    .Select(p => (p.Id, p.Name, p.Description, Agent: new RegistryBackedAgent(p.Id, p.Name, p.Description, holder)))
    .ToList();

foreach (var h in hosted) builder.Services.AddA2AServer(h.Agent);

var app = builder.Build();
holder.Value = app.Services.GetRequiredService<IServiceScopeFactory>();

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "LlmLab.AgentHost (A2A)",
    agents = hosted.Select(h => new
    {
        id = h.Id,
        endpoint = $"{publicBase}/a2a/{h.Id}",
        card = $"{publicBase}/a2a/{h.Id}/.well-known/agent-card.json",
    }),
}));

foreach (var h in hosted)
{
    var card = new AgentCard
    {
        Name = h.Name,
        Description = string.IsNullOrWhiteSpace(h.Description) ? h.Name : h.Description,
        Version = "1.0",
        DefaultInputModes = ["text"],
        DefaultOutputModes = ["text"],
        Capabilities = new AgentCapabilities { Streaming = true },
        SupportedInterfaces =
        [
            new AgentInterface
            {
                Url = $"{publicBase}/a2a/{h.Id}",
                ProtocolBinding = ProtocolBindingNames.HttpJson,
                ProtocolVersion = "1.0",
            },
        ],
    };

    app.MapA2AHttpJson(h.Agent, $"/a2a/{h.Id}");
    app.MapWellKnownAgentCard(card, $"/a2a/{h.Id}");
    app.Logger.LogInformation("A2A ajanı yayınlandı: {Id} -> {Url}/a2a/{Id}", h.Id, publicBase, h.Id);
}

app.Run();
