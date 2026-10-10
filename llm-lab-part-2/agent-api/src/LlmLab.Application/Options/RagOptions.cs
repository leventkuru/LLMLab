using System.ComponentModel.DataAnnotations;

namespace LlmLab.Application.Options;

/// <summary>
/// RAG ayarları ("Rag" bölümü; ortam değişkeni olarak Rag__MinScore gibi de verilebilir).
/// Varsayılanlar qwen3-embedding ağırlıkları içindir; embedding modelini değiştirirseniz ön ekleri de modelin
/// belgesine göre güncelleyin, yanlış ön ek aramanın kalitesini sessizce düşürür.
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>
    /// Sorgunun başına eklenen görev talimatı. Qwen3-Embedding sorgu ile belgeyi farklı biçimlendirmeyi bekler:
    /// sorguya talimat eklenir, belgeye hiçbir şey eklenmez. (nomic-embed-text için: "search_query: " / "search_document: ".)
    /// </summary>
    public string QueryPrefix { get; set; } = "Instruct: Given a question, retrieve the passage that answers it\nQuery:";

    /// <summary>Belge parçasının başına eklenen metin.</summary>
    public string DocumentPrefix { get; set; } = "";

    /// <summary>Bir parçanın hedef uzunluğu (karakter). Küçük parça = daha odaklı vektör, ama cevap bölünebilir.</summary>
    [Range(100, 4000)] public int ChunkChars { get; set; } = 600;

    /// <summary>Ardışık parçalar arasındaki örtüşme (karakter).</summary>
    [Range(0, 1000)] public int OverlapChars { get; set; } = 100;

    /// <summary>Ollama'ya tek istekte gönderilen parça sayısı.</summary>
    [Range(1, 128)] public int EmbedBatchSize { get; set; } = 16;

    /// <summary>Yüklenebilecek en büyük dosya (bayt).</summary>
    [Range(1, 200 * 1024 * 1024)] public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Ajan aramasında modele verilecek en fazla parça sayısı.</summary>
    [Range(1, 20)] public int ChatTopK { get; set; } = 4;

    /// <summary>
    /// Bir parçanın kaynak sayılması için asgari benzerlik. Ölçüme dayanır: ilgili parçalar 0,44 ve üstü, alakasız
    /// sorular en fazla ~0,40 aldı. Altındakiler modele hiç verilmez; aksi halde model alakasız metne dayanarak
    /// güvenle yanlış cevap üretir.
    /// </summary>
    [Range(0, 1)] public double MinScore { get; set; } = 0.42;

    /// <summary>Anahtar kelimeyle bulunan parça için daha düşük asgari benzerlik (kelime eşleşmesi ilgiyi kanıtlar).</summary>
    [Range(0, 1)] public double KeywordFloor { get; set; } = 0.30;

    /// <summary>Sıralamada, sorgudaki her ayırt edici kelimeyi içeren parçaya (en fazla 3 kelime) verilen bonus.</summary>
    [Range(0, 0.5)] public double KeywordBoost { get; set; } = 0.05;

    /// <summary>Bir parça sohbette kaynak sayılabilir mi?</summary>
    public bool IsRelevant(double score, int keywordHits) =>
        score >= MinScore || (keywordHits > 0 && score >= KeywordFloor);
}
