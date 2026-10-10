namespace LlmLab.Domain.Conversations;

/// <summary>
/// Bir cevabın dayandığı belge parçasının anlık görüntüsü (değer nesnesi).
/// Belge sonradan silinse de sohbetteki atıflar bozulmasın diye metin ve belge adı o anki halleriyle kopyalanır.
/// </summary>
/// <param name="Score">Cosine benzerliği (1'e yakın = çok benzer).</param>
/// <param name="KeywordHits">Sorgudaki kaç ayırt edici kelimeyi içerdiği (0 = yalnızca vektörle bulundu).</param>
public sealed record SourceReference(
    long ChunkId,
    Guid DocumentId,
    string FileName,
    int Ordinal,
    string Text,
    double Score,
    int KeywordHits = 0);
