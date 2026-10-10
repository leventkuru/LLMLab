using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using LlmLab.Application.Telemetry;
using LlmLab.Infrastructure.Agents;
using LlmLab.Infrastructure.Ai;
using LlmLab.Infrastructure.Files;
using LlmLab.Infrastructure.Mcp;
using LlmLab.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace LlmLab.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>Altyapı katmanının kayıtları: veritabanı, Ollama istemcileri, ajan, dosya işleme, sağlık kontrolleri.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OllamaOptions>().Bind(configuration.GetSection(OllamaOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        // Veritabanı
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(
            configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"'{ConnectionStringName}' bağlantı dizesi tanımlı değil.")));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IChunkSearcher, ChunkSearcher>();

        // Ollama: sohbet ve embedding için iki ayrı istemci (OllamaApiClient hem IChatClient hem IEmbeddingGenerator uygular).
        // Her model çağrısı bir iz aralığı + metrik üretir (gen_ai.* anlamsal kuralları: model, token kullanımı, süre).
        // İstem/cevap metinleri yalnızca Telemetry:CaptureSensitiveData açıksa izlere yazılır.
        services.AddSingleton<IChatClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var telemetry = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            IChatClient client = new OllamaApiClient(new Uri(o.BaseUrl), o.ChatModel);

            // Model araç çağrısını bazen düz metin olarak yazıyor (araç hiç çalışmıyor, sonuç uyduruluyor): onu gerçek
            // çağrıya çeviren sarmalayıcı, Ollama istemcisinin hemen üstünde; işlev çağırma katmanı bunun da üstündedir.
            client = new TextToolCallRecoveryClient(client, sp.GetService<ILoggerFactory>()?.CreateLogger<TextToolCallRecoveryClient>());
            return client.AsBuilder()
                .UseOpenTelemetry(sp.GetService<ILoggerFactory>(), LlmLabTelemetry.AiSourceName,
                    c => c.EnableSensitiveData = telemetry.CaptureSensitiveData)
                .Build();
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var telemetry = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            IEmbeddingGenerator<string, Embedding<float>> generator = new OllamaApiClient(new Uri(o.BaseUrl), o.EmbeddingModel);
            return generator.AsBuilder()
                .UseOpenTelemetry(sp.GetService<ILoggerFactory>(), LlmLabTelemetry.AiSourceName,
                    c => c.EnableSensitiveData = telemetry.CaptureSensitiveData)
                .Build();
        });
        services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();

        // MCP: araçlar ajanın içinde değil, ayrı MCP sunucularındadır. Registry bağlantıları ve keşfedilen araçları
        // tutar (tekil); katalog bunları tura göre ajana verir. Zaman aşımlarını kendimiz yönettiğimiz için "mcp"
        // HttpClient'ında genel zaman aşımı kapalıdır. İz bağlamı (traceparent) HTTP istemci izlemesiyle sunuculara taşınır.
        services.AddOptions<McpOptions>().Bind(configuration.GetSection(McpOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpClient("mcp", c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<AgentToolCatalog>();
        services.AddSingleton<IToolInventory, McpToolInventory>();

        // Alt ajan görevleri (koordinatör): faz 2'de süreç içi; faz 3'te aynı port Dapr ile uygulanacak.
        services.AddOptions<SubAgentOptions>().Bind(configuration.GetSection(SubAgentOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ISubAgentRunner, InMemorySubAgentRunner>();
        services.AddScoped<SubAgentTools>();

        // Ajan çalıştırma: yerel çalışma zamanı (MafChatAgent) ya da A2A ile uzak servis. IChatAgent, kayıt
        // defterindeki "Hosting" değerine göre birine yönlendirir; ILocalChatAgent her zaman yerel olandır (A2A
        // servisi kendini uzağa çağırıp döngüye girmesin diye ayrı arayüz).
        services.AddOptions<A2aOptions>().Bind(configuration.GetSection(A2aOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpClient("a2a", c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddScoped<ILocalChatAgent, MafChatAgent>();
        services.AddScoped<A2aChatAgent>();
        services.AddScoped<IChatAgent, RoutingChatAgent>();

        services.AddSingleton<ITextExtractor, TextExtractor>();

        services.AddHttpClient();
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database")
            .AddCheck<OllamaHealthCheck>("ollama");

        return services;
    }

    /// <summary>
    /// Bekleyen migration'ları uygular ve başlangıç ajanlarını ekler. Geliştirmede ya da
    /// Database:MigrateOnStartup=true ile açılışta çağrılır.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
        await AgentSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>(), ct);
    }
}
