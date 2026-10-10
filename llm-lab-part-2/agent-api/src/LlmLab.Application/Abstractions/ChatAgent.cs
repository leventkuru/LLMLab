using LlmLab.Domain.Agents;
using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Abstractions;

/// <summary>
/// Bir turda ajanın kimliği: kayıt defterindeki tanımın çalışma için gereken kısmı. Profil verilmezse varsayılan
/// genel asistan (tüm araçlar, ek talimat yok) kullanılır.
/// </summary>
public sealed record AgentProfile(
    string Id, string DisplayName, string Instructions, IReadOnlyList<string> AllowedTools, bool CanDelegate,
    AgentHosting Hosting)
{
    public static AgentProfile Default { get; } =
        new(Conversation.DefaultAgentId, "Asistan", "", [], false, AgentHosting.Local);

    public static AgentProfile From(AgentDefinition d) =>
        new(d.Id, d.DisplayName, d.Instructions, d.AllowedTools, d.CanDelegate, d.Hosting);
}

/// <summary>
/// Ajana verilen istek. SessionState, önceki turlardan kalan serileştirilmiş ajan oturumudur (yoksa null).
/// ExtraInstructions yalnızca bu tura ek talimattır (ör. iki ajanlı diyalogda "kısa konuş").
/// </summary>
public sealed record AgentRequest(
    string Message, string? SessionState, bool Think, bool UseDocuments, AgentProfile? Profile = null,
    string? ExtraInstructions = null);

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
/// Sohbet ajanı portu. Uygulama katmanı ajanın NASIL çalıştığını (hangi çatı, hangi model, hangi araçlar, bu API'nin
/// içinde mi A2A ile uzakta mı) bilmez; yalnızca istek verir ve olay akışı alır. Bu sayede çatı ve konum değiştirilebilir.
/// </summary>
public interface IChatAgent
{
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct);
}

/// <summary>
/// Ajanı HER ZAMAN bu süreçte çalıştıran uygulama (hiç yönlendirme yapmaz). A2A servisi (ajanı başkalarına açan taraf)
/// bunu kullanır; aksi halde uzağa yönlendirilen bir ajan kendini yeniden uzağa çağırıp sonsuz döngüye girerdi.
/// </summary>
public interface ILocalChatAgent : IChatAgent;
