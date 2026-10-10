using LlmLab.Domain.Common;

namespace LlmLab.Domain.Documents;

/// <summary>Bilgi tabanına yüklenmiş bir belge (toplama kökü). Parçaları ve vektörleri onunla birlikte yaşar ve ölür.</summary>
public sealed class KnowledgeDocument
{
    public const int MaxFileNameLength = 260;

    private readonly List<DocumentChunk> _chunks = [];

    private KnowledgeDocument() { } // EF Core

    public Guid Id { get; private set; }
    public string FileName { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public IReadOnlyList<DocumentChunk> Chunks => _chunks;

    public static KnowledgeDocument Create(string fileName, long sizeBytes, DateTime now)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Dosya adı boş olamaz.");
        if (sizeBytes <= 0)
            throw new DomainException("Dosya boş olamaz.");

        return new KnowledgeDocument
        {
            Id = Guid.CreateVersion7(),
            FileName = name.Length <= MaxFileNameLength ? name : name[..MaxFileNameLength],
            SizeBytes = sizeBytes,
            CreatedAt = now,
        };
    }

    public void AddChunk(string text, float[] embedding)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Parça metni boş olamaz.");
        // Değişmez kural: yanlış boyutlu vektör kaydedilirse arama sessizce bozulur; bu yüzden burada durdurulur.
        if (embedding.Length != EmbeddingSpec.Dimensions)
            throw new DomainException(
                $"Embedding boyutu {embedding.Length}, beklenen {EmbeddingSpec.Dimensions}. " +
                "Embedding modelini değiştirdiyseniz EmbeddingSpec ve kolon boyutunu da güncelleyin.");

        _chunks.Add(new DocumentChunk(_chunks.Count, text, embedding));
    }
}
