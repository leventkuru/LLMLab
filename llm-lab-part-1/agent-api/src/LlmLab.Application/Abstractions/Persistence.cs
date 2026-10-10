using LlmLab.Application.Features.Conversations;
using LlmLab.Application.Features.Documents;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;

namespace LlmLab.Application.Abstractions;

/// <summary>İş birimi: yapılan tüm değişiklikler tek işlemde kaydedilir.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}

public interface IConversationRepository
{
    /// <summary>Yazma tarafı: izlenen sohbet (mesajlar yüklenmez; yeni mesaj eklemek için gerekmez).</summary>
    Task<Conversation?> GetAsync(Guid id, CancellationToken ct);

    void Add(Conversation conversation);

    /// <summary>Okuma tarafı: arayüz için hazır projeksiyonlar (varlık yüklemeden).</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct);

    Task<ConversationDetail?> GetDetailAsync(Guid id, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public interface IDocumentRepository
{
    void Add(KnowledgeDocument document);

    Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Bir arama sonucu satırı. Distance: cosine mesafesi (0 = aynı yön, 1 = ilgisiz, 2 = zıt).</summary>
public sealed record ChunkMatch(long ChunkId, Guid DocumentId, string FileName, int Ordinal, string Text, double Distance);

/// <summary>Parça arama portu: vektör yakınlığı ve kelime içerme sorguları. Uygulaması veritabanına aittir.</summary>
public interface IChunkSearcher
{
    /// <summary>Sorgu vektörüne en yakın <paramref name="top"/> parça.</summary>
    Task<IReadOnlyList<ChunkMatch>> NearestAsync(float[] queryVector, int top, CancellationToken ct);

    /// <summary>Metninde <paramref name="term"/> geçen parçalar (en fazla <paramref name="limit"/>), mesafeleriyle.</summary>
    Task<IReadOnlyList<ChunkMatch>> ContainingAsync(string term, float[] queryVector, int limit, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}
