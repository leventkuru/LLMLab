namespace LlmLab.Domain.Conversations;

/// <summary>
/// Kullanıcıya gösterilen sohbet kaydındaki tek mesaj. Ajanın çalışma belleğinden (AgentSession) ayrıdır:
/// bu, arayüzün okuduğu "transkript"tir; düşünce metni, süre ve kaynaklar gibi gösterim verilerini taşır.
/// </summary>
public sealed class ConversationMessage
{
    private ConversationMessage() { } // EF Core

    internal ConversationMessage(
        MessageRole role, string content, string? thinking, double? thinkSeconds,
        IReadOnlyList<SourceReference>? sources, IReadOnlyList<ToolCallReference>? toolCalls, DateTime createdAt)
    {
        Role = role;
        Content = content;
        Thinking = thinking;
        ThinkSeconds = thinkSeconds;
        Sources = sources;
        ToolCalls = toolCalls;
        CreatedAt = createdAt;
    }

    public long Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public MessageRole Role { get; private set; }
    public string Content { get; private set; } = "";

    /// <summary>Modelin iç akıl yürütmesi. Yalnızca gösterim içindir, modele geri gönderilmez.</summary>
    public string? Thinking { get; private set; }
    public double? ThinkSeconds { get; private set; }

    /// <summary>null = belge araması yapılmadı; boş liste = arandı ama yeterince benzer parça bulunamadı.</summary>
    public IReadOnlyList<SourceReference>? Sources { get; private set; }

    /// <summary>Bu cevap sırasında ajanın çağırdığı araçlar. null = kayıt tutulmadı (eski mesaj) ya da araç çağrılmadı.</summary>
    public IReadOnlyList<ToolCallReference>? ToolCalls { get; private set; }

    public DateTime CreatedAt { get; private set; }
}
