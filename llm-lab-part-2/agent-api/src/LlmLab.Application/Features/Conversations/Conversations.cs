using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Features.Conversations;

// ---- Okuma modelleri (arayüzün gördüğü biçim) ------------------------------------------------------------

public sealed record ConversationSummary(Guid Id, string Title, DateTime UpdatedAt, string AgentId = Conversation.DefaultAgentId);

public sealed record MessageView(
    MessageRole Role, string Content, string? Thinking, double? ThinkSecs, IReadOnlyList<SourceReference>? Sources,
    IReadOnlyList<ToolCallReference>? ToolCalls);

public sealed record ConversationDetail(
    Guid Id, string Title, IReadOnlyList<MessageView> Messages, string AgentId = Conversation.DefaultAgentId);

// ---- Kullanım senaryoları --------------------------------------------------------------------------------

public sealed class ListConversationsHandler(IConversationRepository conversations)
{
    public Task<IReadOnlyList<ConversationSummary>> HandleAsync(CancellationToken ct) => conversations.ListAsync(ct);
}

public sealed class GetConversationHandler(IConversationRepository conversations)
{
    public async Task<ConversationDetail> HandleAsync(Guid id, CancellationToken ct) =>
        await conversations.GetDetailAsync(id, ct) ?? throw new NotFoundException("Sohbet bulunamadı.");
}

public sealed class DeleteConversationHandler(IConversationRepository conversations)
{
    public async Task HandleAsync(Guid id, CancellationToken ct)
    {
        if (!await conversations.DeleteAsync(id, ct)) throw new NotFoundException("Sohbet bulunamadı.");
    }
}
