using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LlmLab.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LlmLab.AgentHost;

/// <summary>
/// Kayıt defterindeki bir ajanı A2A sunucusuna "AIAgent" olarak sunan bağdaştırıcı. İş, uygulamanın kendi çalışma
/// zamanına (<see cref="ILocalChatAgent"/>: talimat, izinli araçlar, MCP) devredilir; böylece uzak ve yerel ajan AYNI
/// davranır. Ajan tanımı her istekte kayıt defterinden okunur: talimat/araç değişikliği hemen geçerli olur
/// (yeni bir ajan eklemek ise servisin yeniden başlatılmasını ister: rotalar açılışta kurulur).
/// </summary>
/// <summary>DI kapsam fabrikasına Build'den sonra erişmek için tutucu (ajanlar servis kaydından ÖNCE kurulur).</summary>
internal sealed class ScopeFactoryHolder
{
    public IServiceScopeFactory? Value { get; set; }
}

internal sealed class RegistryBackedAgent(string agentId, string displayName, string description, ScopeFactoryHolder holder) : AIAgent
{
    /// <summary>A2A bağlamı (contextId) başına oturum: ajan oturumunun serileştirilmiş hali.</summary>
    private sealed class ChatSession : AgentSession
    {
        public string? State { get; set; }
    }

    // Ad, A2A sunucu kaydının anahtarıdır: kararlı olsun diye görünen ad değil kimliktir.
    public override string? Name => agentId;
    public override string? Description => string.IsNullOrWhiteSpace(description) ? displayName : description;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new ChatSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var state = (session as ChatSession)?.State;
        return new(JsonSerializer.SerializeToElement(new { state }, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var session = new ChatSession();
        if (serializedState.ValueKind == JsonValueKind.Object &&
            serializedState.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
        {
            session.State = s.GetString();
        }
        return new(session);
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in RunCoreStreamingAsync(messages, session, options, cancellationToken))
        {
            text.Append(update.Text);
        }
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, text.ToString()));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chat = session as ChatSession ?? new ChatSession();

        // A2A her çağrıda yalnızca yeni kullanıcı mesajını gönderir; önceki turlar oturum durumundadır.
        var message = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(message))
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "Boş mesaj.");
            yield break;
        }

        var scopes = holder.Value ?? throw new InvalidOperationException("Servis henüz hazır değil.");
        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var definition = await repo.GetAsync(agentId, cancellationToken);
        if (definition is null || !definition.Enabled)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"Ajan kullanılamıyor: {agentId}");
            yield break;
        }

        var local = scope.ServiceProvider.GetRequiredService<ILocalChatAgent>();
        var request = new AgentRequest(message, chat.State, Think: false, UseDocuments: true, AgentProfile.From(definition));

        await foreach (var evt in local.RunAsync(request, cancellationToken))
        {
            switch (evt)
            {
                case TextDelta t:
                    yield return new AgentResponseUpdate(ChatRole.Assistant, t.Text);
                    break;
                case SessionSaved s:
                    chat.State = s.State;
                    break;
            }
        }
    }
}
