using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LlmLab.Infrastructure.Ai;

/// <summary>Ollama sunucusuna erişilebiliyor mu? (/health uç noktasında görünür.)</summary>
internal sealed class OllamaHealthCheck(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(OllamaHealthCheck));
            client.Timeout = TimeSpan.FromSeconds(3);
            using var response = await client.GetAsync(new Uri(new Uri(options.Value.BaseUrl), "/api/version"), ct);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Ollama {(int)response.StatusCode} döndürdü.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Ollama'ya erişilemiyor.", e);
        }
    }
}
