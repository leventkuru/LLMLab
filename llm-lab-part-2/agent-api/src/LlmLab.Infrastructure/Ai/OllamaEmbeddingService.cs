using LlmLab.Application.Abstractions;
using Microsoft.Extensions.AI;

namespace LlmLab.Infrastructure.Ai;

/// <summary>
/// IEmbeddingService'in Microsoft.Extensions.AI üzerindeki uygulaması. Sağlayıcı (Ollama, OpenAI, Azure) yalnızca
/// DI kaydındaki IEmbeddingGenerator'dır; bu sınıf onu uygulama katmanının portuna uyarlar.
/// </summary>
internal sealed class OllamaEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbeddingService
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var result = await generator.GenerateAsync(inputs, cancellationToken: ct);
        return result.Select(e => e.Vector.ToArray()).ToList();
    }
}
