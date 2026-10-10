namespace LlmLab.Domain.Documents;

/// <summary>Belgenin bir parçası ve o parçanın embedding vektörü.</summary>
public sealed class DocumentChunk
{
    private DocumentChunk() { } // EF Core

    internal DocumentChunk(int ordinal, string text, float[] embedding)
    {
        Ordinal = ordinal;
        Text = text;
        Embedding = embedding;
    }

    public long Id { get; private set; }
    public Guid DocumentId { get; private set; }

    /// <summary>Parçanın belgedeki sırası (0'dan başlar).</summary>
    public int Ordinal { get; private set; }
    public string Text { get; private set; } = "";

    /// <summary>
    /// Embedding vektörü. Boyut embedding modeline bağlıdır; farklı modellerin vektörleri karşılaştırılamaz,
    /// model değişirse tüm belgeler yeniden gömülmelidir.
    /// </summary>
    public float[] Embedding { get; private set; } = [];
}
