namespace LlmLab.Domain.Documents;

/// <summary>
/// Embedding vektörünün şeması. Boyut kullanılan embedding modeline bağlıdır (qwen3-embedding: 1024,
/// nomic-embed-text: 768) ve veritabanı kolonunun tipine (vector(N)) yansır; model değişirse buradaki değer
/// ve bir migration birlikte değişmeli, belgeler yeniden gömülmelidir.
/// </summary>
public static class EmbeddingSpec
{
    public const int Dimensions = 1024;
}
