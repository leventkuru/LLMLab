using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A;
using LlmLab.Application.Abstractions;
using LlmLab.Domain.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmLab.Infrastructure.Agents;

/// <summary>A2A ayarları ("A2A" bölümü): uzak ajanları barındıran servisin adresi.</summary>
public sealed class A2aOptions
{
    public const string SectionName = "A2A";

    /// <summary>A2A servisinin kök adresi; ajanlar {ServiceUrl}/a2a/{ajanKimliği} altındadır.</summary>
    [Required] public string ServiceUrl { get; set; } = "http://localhost:5201";

    /// <summary>Bir uzak ajan turunun en uzun süresi (saniye).</summary>
    [Range(5, 1800)] public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Hosting=Remote olan ajanları A2A (Agent-to-Agent) protokolüyle ayrı servisten çalıştırır. Uygulama katmanı için
/// fark yoktur: yine bir <see cref="IChatAgent"/>'tır ve olay akışı üretir. Farklar (belgelenmiş sınırlar):
///  - uzak ajanın düşünme akışı ve araç çağrıları görünmez (A2A yalnızca mesaj/artefakt taşır);
///  - kaynak listesi dönmez (belge atıfları metin içindedir);
///  - oturum, A2A bağlamıdır (contextId); durum olarak serileştirilir.
/// </summary>
internal sealed class A2aChatAgent(
    IOptions<A2aOptions> options, IHttpClientFactory httpFactory, ILogger<A2aChatAgent> log) : IChatAgent
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var profile = request.Profile ?? AgentProfile.Default;
        var opt = options.Value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opt.TimeoutSeconds));

        var (agent, error) = await ConnectAsync(opt, profile.Id, timeout.Token);
        if (agent is null)
        {
            yield return new TextDelta($"Uzak ajana ulaşılamadı ({profile.DisplayName}): {error}. A2A servisi çalışıyor mu?");
            yield break;
        }

        var session = string.IsNullOrEmpty(request.SessionState)
            ? await agent.CreateSessionAsync(timeout.Token)
            : await agent.DeserializeSessionAsync(JsonDocument.Parse(request.SessionState).RootElement, cancellationToken: timeout.Token);

        // A2A mesajı talimat taşımaz: tura özel ek talimat (ör. diyalog kuralları) mesajın önüne konur.
        var text = string.IsNullOrWhiteSpace(request.ExtraInstructions)
            ? request.Message
            : $"{request.ExtraInstructions.Trim()}\n\n{request.Message}";

        await foreach (var update in agent.RunStreamingAsync(text, session, cancellationToken: timeout.Token))
        {
            if (!string.IsNullOrEmpty(update.Text)) yield return new TextDelta(update.Text);
        }

        var state = await agent.SerializeSessionAsync(session, cancellationToken: timeout.Token);
        yield return new SessionSaved(state.GetRawText());
    }

    /// <summary>Ajan kartını çözüp uzak ajanı bir AIAgent olarak bağlar. Hata fırlatmaz; (null, neden) döner.</summary>
    private async Task<(AIAgent? Agent, string? Error)> ConnectAsync(A2aOptions opt, string agentId, CancellationToken ct)
    {
        try
        {
            var baseUri = new Uri($"{opt.ServiceUrl.TrimEnd('/')}/a2a/{agentId}/");
            var http = httpFactory.CreateClient("a2a");
            var card = await new A2ACardResolver(baseUri, http).GetAgentCardAsync(ct);
            var agent = card.AsAIAgent(options: new A2AClientOptions { PreferredBindings = [ProtocolBindingNames.HttpJson] });
            return (agent, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "A2A ajanına bağlanılamadı: {Agent} ({Url})", agentId, opt.ServiceUrl);
            return (null, e.Message);
        }
    }
}

/// <summary>
/// İstek ajanın nerede çalıştığına göre (kayıt defterindeki Hosting) yerel çalışma zamanına ya da A2A istemcisine
/// yönlendirilir. Uygulama katmanı yalnızca IChatAgent'ı bilir.
/// </summary>
internal sealed class RoutingChatAgent(ILocalChatAgent local, A2aChatAgent remote) : IChatAgent
{
    public IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct) =>
        request.Profile?.Hosting == AgentHosting.Remote
            ? remote.RunAsync(request, ct)
            : local.RunAsync(request, ct);
}
