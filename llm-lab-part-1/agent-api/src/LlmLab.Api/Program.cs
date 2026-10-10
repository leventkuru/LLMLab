using System.Text.Json;
using System.Text.Json.Serialization;
using LlmLab.Api.Endpoints;
using LlmLab.Api.Middleware;
using LlmLab.Api.Telemetry;
using LlmLab.Application;
using LlmLab.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Bileşim kökü (composition root): katmanlar burada, yalnızca burada birbirine bağlanır.
builder.Services
    .AddApplication(builder.Configuration)
    .AddInfrastructure(builder.Configuration);

builder.AddLlmLabTelemetry(); // iz + metrik + günlük (OpenTelemetry)

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Enum'lar "user"/"assistant" gibi küçük harfli metin olarak serileştirilir (arayüzle sözleşme).
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// CORS izinli kökenler ayardan gelir (Cors:Origins); kodda sabit değildir.
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();

// Bekleyen migration'ları açılışta uygula: geliştirmede ya da Database:MigrateOnStartup=true verilince
// (compose / yerel Kubernetes). Gerçek üretimde bunu ayrı bir dağıtım adımı olarak yapın.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    await app.Services.MigrateDatabaseAsync();

if (app.Environment.IsDevelopment()) app.MapOpenApi(); // /openapi/v1.json

app.MapHealthChecks("/health");
app.MapGet("/", () => "LlmLab.Api (Agent Framework) çalışıyor. Sağlık: /health");
app.MapLlmLabEndpoints();

app.Run();

public partial class Program; // entegrasyon testleri (WebApplicationFactory) için
