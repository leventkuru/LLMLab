using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using LlmLab.Application.Telemetry;
using LlmLab.Infrastructure.Ai;
using LlmLab.Infrastructure.Files;
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
        services.AddScoped<IChunkSearcher, ChunkSearcher>();

        // Ollama: sohbet ve embedding için iki ayrı istemci (OllamaApiClient hem IChatClient hem IEmbeddingGenerator uygular).
        // Her model çağrısı bir iz aralığı + metrik üretir (gen_ai.* anlamsal kuralları: model, token kullanımı, süre).
        // İstem/cevap metinleri yalnızca Telemetry:CaptureSensitiveData açıksa izlere yazılır.
        services.AddSingleton<IChatClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            var telemetry = sp.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            IChatClient client = new OllamaApiClient(new Uri(o.BaseUrl), o.ChatModel);
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

        // Ajan: IDocumentSearch (kapsamlı, DbContext'e bağlı) kullandığı için kapsamlıdır.
        services.AddScoped<IChatAgent, MafChatAgent>();

        services.AddSingleton<ITextExtractor, TextExtractor>();

        services.AddHttpClient();
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database")
            .AddCheck<OllamaHealthCheck>("ollama");

        return services;
    }

    /// <summary>Bekleyen migration'ları uygular. Geliştirmede ya da Database:MigrateOnStartup=true ile açılışta çağrılır.</summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(ct);
    }
}
