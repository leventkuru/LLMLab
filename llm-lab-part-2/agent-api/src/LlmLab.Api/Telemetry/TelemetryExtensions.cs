using System.Reflection;
using LlmLab.Application.Options;
using LlmLab.Application.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LlmLab.Api.Telemetry;

/// <summary>
/// OpenTelemetry SDK kurulumu (bileşim kökü). Üç sinyal: iz (trace), metrik, günlük (log); üçü de aynı kaynak
/// bilgisiyle (service.name vb.) OTLP toplayıcıya gider.
///
/// Neyi ne üretiyor?
///  - ASP.NET Core / HttpClient / SqlClient / Runtime: kütüphane enstrümantasyonları (HTTP istekleri, Ollama çağrıları,
///    SQL sorguları, GC ve iş parçacığı metrikleri). /health izlenmez, gürültü yapar.
///  - "LlmLab" kaynağı: uygulamanın kendi aralıkları (chat.turn, rag.search, documents.upload) ve llmlab.* metrikleri.
///  - "LlmLab.AI": Microsoft.Extensions.AI'ın model çağrıları (gen_ai.* kuralları, token kullanımı).
///  - "LlmLab.Agent": Agent Framework'ün ajan çalıştırmaları.
/// Hepsi tek bir izin altında iç içe görünür: HTTP isteği > chat.turn > invoke_agent > model/SQL/HTTP çağrıları.
/// </summary>
internal static class TelemetryExtensions
{
    public static WebApplicationBuilder AddLlmLabTelemetry(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        if (!options.Enabled) return builder;

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        var resource = ResourceBuilder.CreateDefault()
            .AddService(options.ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName)
            .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]);

        var otlp = !string.IsNullOrWhiteSpace(options.OtlpEndpoint) ? new Uri(options.OtlpEndpoint) : null;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(options.ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                        o.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation(o => o.RecordException = true)
                    .AddSource(LlmLabTelemetry.SourceName, LlmLabTelemetry.AiSourceName, LlmLabTelemetry.AgentSourceName);

                if (otlp is not null) tracing.AddOtlpExporter(o => o.Endpoint = otlp);
                if (options.Console) tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(LlmLabTelemetry.SourceName, LlmLabTelemetry.AiSourceName, LlmLabTelemetry.AgentSourceName);

                if (otlp is not null) metrics.AddOtlpExporter(o => o.Endpoint = otlp);
                if (options.Console) metrics.AddConsoleExporter();
            });

        // Günlükler de OpenTelemetry üzerinden gider: her kayıt, içinde üretildiği iz/aralığın kimliğini (TraceId, SpanId)
        // taşır, böylece bir günlük satırından ilgili izi bulabilirsiniz.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (otlp is not null) logging.AddOtlpExporter(o => o.Endpoint = otlp);
            if (options.Console) logging.AddConsoleExporter();
        });

        return builder;
    }
}
