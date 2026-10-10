using LlmLab.Domain.Documents;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LlmLab.Infrastructure.Persistence.Configurations;

internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> b)
    {
        b.ToTable("Documents");
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedNever();
        b.Property(d => d.FileName).HasMaxLength(KnowledgeDocument.MaxFileNameLength);

        // Belge silinince parçaları (ve vektörleri) de silinir. ExecuteDelete EF'in cascade'ini atladığı için
        // asıl güvence veritabanındaki ON DELETE CASCADE kısıtıdır.
        b.HasMany(d => d.Chunks).WithOne().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(d => d.Chunks).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> b)
    {
        b.ToTable("Chunks");
        b.HasKey(c => c.Id);
        b.HasIndex(c => new { c.DocumentId, c.Ordinal });

        // Etki alanı düz float[] bilir; SQL Server 2025'in yerel VECTOR tipine dönüşüm yalnızca burada yapılır.
        b.Property(c => c.Embedding)
            .HasConversion(
                v => new SqlVector<float>(v),
                v => v.Memory.ToArray(),
                new ValueComparer<float[]>(
                    (x, y) => ReferenceEquals(x, y) || (x != null && y != null && x.SequenceEqual(y)),
                    v => v.Length,
                    v => v.ToArray()))
            .HasColumnType($"vector({EmbeddingSpec.Dimensions})");
    }
}
