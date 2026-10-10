using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Abstractions;

/// <summary>Metni vektöre çevirir (embedding). Uygulaması dış bir model sunucusuna aittir.</summary>
public interface IEmbeddingService
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

/// <summary>Yüklenen dosyadan düz metin çıkarır.</summary>
public interface ITextExtractor
{
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <exception cref="InvalidDataException">Dosya bozuk, şifreli ya da okunamıyor.</exception>
    string Extract(string fileName, byte[] content);
}

/// <summary>Hibrit (vektör + anahtar kelime) belge araması.</summary>
public interface IDocumentSearch
{
    Task<IReadOnlyList<SourceReference>> SearchAsync(string query, int topK, CancellationToken ct);
}
