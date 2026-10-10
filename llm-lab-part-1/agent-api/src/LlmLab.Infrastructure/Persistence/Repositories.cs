using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Conversations;
using LlmLab.Application.Features.Documents;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace LlmLab.Infrastructure.Persistence;

internal sealed class ConversationRepository(AppDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct) =>
        db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);

    public void Add(Conversation conversation) => db.Conversations.Add(conversation);

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct) =>
        await db.Conversations.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.UpdatedAt))
            .ToListAsync(ct);

    public async Task<ConversationDetail?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Title,
                Messages = x.Messages.OrderBy(m => m.Id)
                    .Select(m => new { m.Role, m.Content, m.Thinking, m.ThinkSeconds, m.Sources, m.ToolCalls })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return c is null
            ? null
            : new ConversationDetail(
                c.Id, c.Title,
                c.Messages.Select(m => new MessageView(m.Role, m.Content, m.Thinking, m.ThinkSeconds, m.Sources, m.ToolCalls)).ToList());
    }

    // Mesajlar veritabanındaki ON DELETE CASCADE ile birlikte silinir.
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;
}

internal sealed class DocumentRepository(AppDbContext db) : IDocumentRepository
{
    public void Add(KnowledgeDocument document) => db.Documents.Add(document);

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct) =>
        await db.Documents.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DocumentSummary(d.Id, d.FileName, d.SizeBytes, d.Chunks.Count, d.CreatedAt))
            .ToListAsync(ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(ct) > 0;
}

/// <summary>
/// Vektör ve kelime araması. Ham SQL kullanır: etki alanı varlığı vektörü düz float[] tutar (SQL Server tipinden
/// bağımsız kalsın diye); VECTOR_DISTANCE ise burada, parametre olarak verilen SqlVector ile çağrılır.
/// SQL tek yerde durur ve bu sınıf entegrasyon testine uygundur.
/// </summary>
internal sealed class ChunkSearcher(AppDbContext db) : IChunkSearcher
{
    // SqlQuery<T> için sonuç satırı: kolon adlarıyla birebir eşleşir.
    private sealed class Row
    {
        public long ChunkId { get; set; }
        public Guid DocumentId { get; set; }
        public string FileName { get; set; } = "";
        public int Ordinal { get; set; }
        public string Text { get; set; } = "";
        public double Distance { get; set; }

        public ChunkMatch ToMatch() => new(ChunkId, DocumentId, FileName, Ordinal, Text, Distance);
    }

    public async Task<IReadOnlyList<ChunkMatch>> NearestAsync(float[] queryVector, int top, CancellationToken ct)
    {
        var vector = new SqlVector<float>(queryVector);
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT TOP ({top}) c.Id AS ChunkId, c.DocumentId, d.FileName, c.Ordinal, c.Text,
                   VECTOR_DISTANCE('cosine', c.Embedding, {vector}) AS Distance
            FROM Chunks c
            JOIN Documents d ON d.Id = c.DocumentId
            ORDER BY Distance
            """).ToListAsync(ct);
        return rows.Select(r => r.ToMatch()).ToList();
    }

    public async Task<IReadOnlyList<ChunkMatch>> ContainingAsync(string term, float[] queryVector, int limit, CancellationToken ct)
    {
        var vector = new SqlVector<float>(queryVector);
        var pattern = "%" + EscapeLike(term) + "%";
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT TOP ({limit}) c.Id AS ChunkId, c.DocumentId, d.FileName, c.Ordinal, c.Text,
                   VECTOR_DISTANCE('cosine', c.Embedding, {vector}) AS Distance
            FROM Chunks c
            JOIN Documents d ON d.Id = c.DocumentId
            WHERE c.Text LIKE {pattern} ESCAPE '\'
            """).ToListAsync(ct);
        return rows.Select(r => r.ToMatch()).ToList();
    }

    public Task<int> CountAsync(CancellationToken ct) => db.Chunks.CountAsync(ct);

    // LIKE özel karakterlerini kaçır; terim kullanıcı girdisinden türer.
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
