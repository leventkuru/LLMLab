using LlmLab.Domain.Common;

namespace LlmLab.Domain.Conversations;

/// <summary>
/// Sohbet (toplama kökü). Hem kullanıcının gördüğü transkripti (Messages) hem de ajanın serileştirilmiş
/// oturum durumunu (AgentSessionState) taşır; ikisi tek işlemde, tutarlı biçimde güncellenir.
/// </summary>
public sealed class Conversation
{
    public const int MaxTitleLength = 60;

    private readonly List<ConversationMessage> _messages = [];

    private Conversation() { } // EF Core

    public Guid Id { get; private set; }
    public string Title { get; private set; } = "";
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Ajan oturumunun serileştirilmiş hali (JSON). Ajanın "çalışma belleğidir": geçmiş mesajlar, araç çağrıları vb.
    /// Etki alanı bu içeriği yorumlamaz, yalnızca saklar; biçimi altyapı katmanındaki ajana aittir.
    /// </summary>
    public string? AgentSessionState { get; private set; }

    public IReadOnlyList<ConversationMessage> Messages => _messages;

    public static Conversation Start(string firstMessage, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(firstMessage))
            throw new DomainException("Mesaj boş olamaz.");

        return new Conversation
        {
            Id = Guid.CreateVersion7(), // zaman sıralı: indeks dostu
            Title = MakeTitle(firstMessage),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public ConversationMessage AddUserMessage(string content, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new DomainException("Mesaj boş olamaz.");
        return Append(new ConversationMessage(MessageRole.User, content, null, null, null, null, now), now);
    }

    public ConversationMessage AddAssistantMessage(
        string content, string? thinking, double? thinkSeconds, IReadOnlyList<SourceReference>? sources,
        IReadOnlyList<ToolCallReference>? toolCalls, DateTime now) =>
        Append(new ConversationMessage(MessageRole.Assistant, content, thinking, thinkSeconds, sources, toolCalls, now), now);

    public void UpdateAgentSessionState(string? state, DateTime now)
    {
        AgentSessionState = state;
        UpdatedAt = now;
    }

    private ConversationMessage Append(ConversationMessage message, DateTime now)
    {
        _messages.Add(message);
        UpdatedAt = now;
        return message;
    }

    public static string MakeTitle(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxTitleLength ? line : line[..MaxTitleLength].TrimEnd() + "…";
    }
}
