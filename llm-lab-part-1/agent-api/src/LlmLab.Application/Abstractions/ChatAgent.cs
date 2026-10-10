using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Abstractions;

/// <summary>Ajana verilen istek. SessionState, önceki turlardan kalan serileştirilmiş ajan oturumudur (yoksa null).</summary>
public sealed record AgentRequest(string Message, string? SessionState, bool Think, bool UseDocuments);

/// <summary>Ajanın bir turu sırasında ürettiği olaylar.</summary>
public abstract record AgentEvent;

/// <summary>Modelin iç akıl yürütmesinden bir parça.</summary>
public sealed record ThinkingDelta(string Text) : AgentEvent;

/// <summary>Asıl cevaptan bir parça.</summary>
public sealed record TextDelta(string Text) : AgentEvent;

/// <summary>Ajan bir araç çağırdı (ör. belge araması).</summary>
public sealed record ToolInvoked(string Name, string? Arguments) : AgentEvent;

/// <summary>Bu turda ajanın kullandığı kaynakların güncel, birikimli listesi.</summary>
public sealed record SourcesFound(IReadOnlyList<SourceReference> Sources) : AgentEvent;

/// <summary>Tur başarıyla bitti; serileştirilmiş yeni oturum durumu. Akış yarıda kesilirse üretilmez.</summary>
public sealed record SessionSaved(string State) : AgentEvent;

/// <summary>
/// Sohbet ajanı portu. Uygulama katmanı ajanın NASIL çalıştığını (hangi çatı, hangi model, hangi araçlar) bilmez;
/// yalnızca istek verir ve olay akışı alır. Bu sayede çatı değiştirilebilir (Agent Framework, LangChain, düz IChatClient).
/// </summary>
public interface IChatAgent
{
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct);
}
