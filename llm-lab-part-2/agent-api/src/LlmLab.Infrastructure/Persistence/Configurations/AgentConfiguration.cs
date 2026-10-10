using System.Text.Json;
using LlmLab.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LlmLab.Infrastructure.Persistence.Configurations;

internal sealed class AgentConfiguration : IEntityTypeConfiguration<AgentDefinition>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AgentDefinition> b)
    {
        b.ToTable("AgentDefinitions");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).HasMaxLength(32).ValueGeneratedNever(); // kimliği kullanıcı verir (slug)
        b.Property(a => a.DisplayName).HasMaxLength(60);
        b.Property(a => a.Description).HasMaxLength(300);
        b.Property(a => a.Instructions).HasMaxLength(AgentDefinition.MaxInstructionsLength);
        b.Property(a => a.Hosting).HasConversion<string>().HasMaxLength(16);

        // İzinli araç adları: küçük bir liste, tek başına sorgulanmaz; JSON kolonu.
        b.Property(a => a.AllowedTools)
            .HasColumnName("AllowedToolsJson")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => (IReadOnlyList<string>)JsonSerializer.Deserialize<List<string>>(v, Json)!,
                new ValueComparer<IReadOnlyList<string>>(
                    (x, y) => ReferenceEquals(x, y) || (x != null && y != null && x.SequenceEqual(y)),
                    v => v.Count,
                    v => v.ToList()));
    }
}
