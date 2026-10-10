using LlmLab.Application.Abstractions;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace LlmLab.Infrastructure.Persistence;

/// <summary>
/// EF Core bağlamı. Aynı zamanda iş birimidir (IUnitOfWork): Application katmanı EF'i değil yalnızca bu arayüzü bilir.
/// DbContext.SaveChangesAsync(CancellationToken) imzası arayüzü örtük olarak karşılar.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
