using System.Text.Json;
using LlmLab.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LlmLab.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("Conversations");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).ValueGeneratedNever(); // Guid'i etki alanı üretir (zaman sıralı, v7)
        b.Property(c => c.Title).HasMaxLength(200);
        b.HasIndex(c => c.UpdatedAt); // sohbet listesi "son güncellenen" sırasıyla okunur

        b.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(c => c.Messages).UsePropertyAccessMode(PropertyAccessMode.Field); // _messages alanı
    }
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<ConversationMessage> b)
    {
        b.ToTable("Messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(m => new { m.ConversationId, m.Id }); // bir sohbetin mesajlarını sırayla okumak için

        // Kaynaklar ayrı tablo değil, mesajla birlikte tek JSON kolonu: onlar o anın anlık görüntüsüdür,
        // tek başlarına sorgulanmazlar. null = arama yapılmadı, "[]" = arandı ama bulunamadı.
        b.Property(m => m.Sources)
            .HasColumnName("SourcesJson")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : (IReadOnlyList<SourceReference>)JsonSerializer.Deserialize<List<SourceReference>>(v, Json)!,
                new ValueComparer<IReadOnlyList<SourceReference>?>(
                    (x, y) => ReferenceEquals(x, y) || (x != null && y != null && x.SequenceEqual(y)),
                    v => v == null ? 0 : v.Count,
                    v => v));

        // Araç çağrıları da aynı yaklaşımla JSON kolonu: ajanın o turda ne yaptığının kalıcı izi.
        b.Property(m => m.ToolCalls)
            .HasColumnName("ToolCallsJson")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : (IReadOnlyList<ToolCallReference>)JsonSerializer.Deserialize<List<ToolCallReference>>(v, Json)!,
                new ValueComparer<IReadOnlyList<ToolCallReference>?>(
                    (x, y) => ReferenceEquals(x, y) || (x != null && y != null && x.SequenceEqual(y)),
                    v => v == null ? 0 : v.Count,
                    v => v));
    }
}
