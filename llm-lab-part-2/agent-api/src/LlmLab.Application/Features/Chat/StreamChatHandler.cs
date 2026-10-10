using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Application.Telemetry;
using LlmLab.Domain.Conversations;
using Microsoft.Extensions.Logging;

namespace LlmLab.Application.Features.Chat;

/// <param name="AgentId">Yeni sohbette kullanılacak ajan (kayıt defteri kimliği); boşsa varsayılan. Var olan sohbette yok sayılır.</param>
public sealed record StreamChatCommand(
    string Message, Guid? ConversationId, bool Think, bool UseDocuments, string? AgentId = null);

/// <summary>Sohbet akışının dış dünyaya (arayüze) giden olayları.</summary>
public abstract record ChatStreamEvent;
public sealed record ConversationStarted(Guid ConversationId) : ChatStreamEvent;
public sealed record ToolCalled(string Name, string? Arguments) : ChatStreamEvent;
public sealed record SourcesAvailable(IReadOnlyList<SourceReference> Sources) : ChatStreamEvent;
public sealed record ThinkingChunk(string Text) : ChatStreamEvent;
public sealed record TokenChunk(string Text) : ChatStreamEvent;

/// <summary>
/// Sohbet turunun orkestratörü. İki ayrı şeyi tutarlı biçimde yönetir:
///   - transkript (Conversation.Messages): kullanıcının gördüğü mesajlar, düşünce metni, kaynaklar, araç çağrıları;
///   - ajan oturumu (Conversation.AgentSessionState): ajanın çalışma belleği.
/// Ajanın nasıl çalıştığını (çatı, model, araçlar) bilmez; yalnızca IChatAgent'ın olay akışını tüketir.
///
/// Gözlemlenebilirlik: her tur "chat.turn" izi ve llmlab.chat.* metrikleri üretir. Ajanın ve modelin iç adımları
/// (model çağrıları, araç çalıştırma, SQL, HTTP) bu izin altında alt aralıklar (span) olarak görünür.
/// </summary>
public sealed class StreamChatHandler(
    IConversationRepository conversations, IAgentRepository agents, IChatAgent agent, IUnitOfWork uow,
    TimeProvider clock, ILogger<StreamChatHandler> log)
{
    public async IAsyncEnumerable<ChatStreamEvent> HandleAsync(
        StreamChatCommand cmd, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Message)) throw new ValidationException("Mesaj boş olamaz.");

        var now = clock.GetUtcNow().UtcDateTime;

        // Önce sohbeti (varsa) ve ajanını çöz: hatalar (bulunamadı, devre dışı) veritabanına bir şey yazılmadan döner.
        Conversation? existing = null;
        if (cmd.ConversationId is { } id)
        {
            existing = await conversations.GetAsync(id, ct) ?? throw new NotFoundException("Sohbet bulunamadı.");
        }
        var agentId = existing?.AgentId ?? (string.IsNullOrWhiteSpace(cmd.AgentId) ? Conversation.DefaultAgentId : cmd.AgentId);
        var profile = await ResolveProfileAsync(agentId, ct);

        Conversation conversation;
        if (existing is not null)
        {
            conversation = existing;
        }
        else
        {
            conversation = Conversation.Start(cmd.Message, now, agentId);
            conversations.Add(conversation);
        }

        // Kullanıcı mesajını akış başlamadan kaydet: model yanıt vermese de soru kaybolmasın.
        conversation.AddUserMessage(cmd.Message, now);
        await uow.SaveChangesAsync(ct);

        // İz: kullanıcı mesajının İÇERİĞİ bilerek yazılmaz (kişisel veri olabilir); yalnızca ayarlar ve sayılar.
        using var activity = LlmLabTelemetry.ActivitySource.StartActivity("chat.turn");
        activity?.SetTag(LlmLabTelemetry.Tags.ConversationId, conversation.Id);
        activity?.SetTag(LlmLabTelemetry.Tags.Think, cmd.Think);
        activity?.SetTag(LlmLabTelemetry.Tags.UseDocuments, cmd.UseDocuments);
        activity?.SetTag(LlmLabTelemetry.Tags.AgentId, profile.Id);

        using var scope = log.BeginScope(new Dictionary<string, object> { ["ConversationId"] = conversation.Id });
        yield return new ConversationStarted(conversation.Id);

        var content = new StringBuilder();
        var thinking = new StringBuilder();
        IReadOnlyList<SourceReference>? sources = null; // null = belge araması yapılmadı
        var toolCalls = new List<ToolCallReference>();
        string? sessionState = null;
        var stopwatch = Stopwatch.StartNew();
        double? thinkStart = null, thinkSeconds = null, firstTokenSeconds = null;
        var completed = false;

        try
        {
            var request = new AgentRequest(cmd.Message, conversation.AgentSessionState, cmd.Think, cmd.UseDocuments, profile);
            await foreach (var evt in agent.RunAsync(request, ct))
            {
                switch (evt)
                {
                    case ThinkingDelta t:
                        thinkStart ??= stopwatch.Elapsed.TotalSeconds;
                        thinking.Append(t.Text);
                        yield return new ThinkingChunk(t.Text);
                        break;

                    case TextDelta t:
                        // İlk cevap tokeni geldiğinde düşünme süresini ve ilk-token gecikmesini sabitle.
                        firstTokenSeconds ??= stopwatch.Elapsed.TotalSeconds;
                        if (thinkStart is { } start && thinkSeconds is null) thinkSeconds = stopwatch.Elapsed.TotalSeconds - start;
                        content.Append(t.Text);
                        yield return new TokenChunk(t.Text);
                        break;

                    case ToolInvoked t:
                        // Konum: şu ana dek yazılmış cevap uzunluğu; arayüz araç satırını metnin bu noktasına koyar.
                        toolCalls.Add(new ToolCallReference(t.Name, t.Arguments, content.Length));
                        LlmLabTelemetry.ToolCalls.Add(1, new KeyValuePair<string, object?>("tool", t.Name));
                        activity?.AddEvent(new ActivityEvent("tool_called", tags: new ActivityTagsCollection { ["tool"] = t.Name }));
                        yield return new ToolCalled(t.Name, t.Arguments);
                        break;

                    case SourcesFound s:
                        sources = s.Sources;
                        yield return new SourcesAvailable(s.Sources);
                        break;

                    case SessionSaved s:
                        sessionState = s.State;
                        break;
                }
            }
            completed = true;
        }
        finally
        {
            RecordOutcome(activity, cmd, completed, ct, stopwatch.Elapsed.TotalSeconds, firstTokenSeconds, thinkSeconds,
                content.Length, thinking.Length, sources, toolCalls.Count, sessionState is not null);

            // Akış bitse de, hata alsa da, kullanıcı "Durdur"a bassa da o ana kadarki cevabı kaydet.
            // İstek iptal edilmiş olabileceği için CancellationToken.None kullanılır.
            // Not: akış yarıda kesildiyse ajan oturum durumu üretmez; transkriptte kısmi cevap vardır ama ajanın
            // belleğinde o tur yoktur (bilinçli ödünleşim: yarım turun belleğe girmesi daha kötü olurdu).
            if (content.Length > 0 || thinking.Length > 0)
            {
                var done = clock.GetUtcNow().UtcDateTime;
                conversation.AddAssistantMessage(
                    content.ToString(),
                    thinking.Length > 0 ? thinking.ToString() : null,
                    thinkSeconds ?? (thinkStart is { } t0 ? stopwatch.Elapsed.TotalSeconds - t0 : null),
                    sources,
                    toolCalls,
                    done);
                if (sessionState is not null) conversation.UpdateAgentSessionState(sessionState, done);
                await uow.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Ajanı kayıt defterinden çözer. Varsayılan ajan kayıt defterinde henüz yoksa (ilk açılış/test) yerleşik profil
    /// kullanılır; diğer kimlikler kayıtlı ve etkin olmalıdır.
    /// </summary>
    private async Task<AgentProfile> ResolveProfileAsync(string agentId, CancellationToken ct)
    {
        var definition = await agents.GetAsync(agentId, ct);
        if (definition is null)
        {
            if (agentId == Conversation.DefaultAgentId) return AgentProfile.Default;
            throw new NotFoundException($"Ajan bulunamadı: {agentId}");
        }
        if (!definition.Enabled) throw new ValidationException($"'{definition.DisplayName}' ajanı devre dışı.");
        return AgentProfile.From(definition);
    }

    /// <summary>Turun sonucunu iz etiketlerine ve metriklere yazar.</summary>
    private static void RecordOutcome(
        Activity? activity, StreamChatCommand cmd, bool completed, CancellationToken ct, double seconds,
        double? firstTokenSeconds, double? thinkSeconds, int answerChars, int thinkingChars,
        IReadOnlyList<SourceReference>? sources, int toolCalls, bool sessionSaved)
    {
        // Sonuç: normal bitti = ok; istemci keserse (Durdur) = canceled; aksi halde hata = failed.
        var outcome = completed ? "ok" : ct.IsCancellationRequested ? "canceled" : "failed";

        if (activity is not null)
        {
            activity.SetTag(LlmLabTelemetry.Tags.Sources, sources?.Count ?? -1); // -1: belge araması yapılmadı
            activity.SetTag(LlmLabTelemetry.Tags.ToolCallCount, toolCalls);
            activity.SetTag(LlmLabTelemetry.Tags.AnswerChars, answerChars);
            activity.SetTag(LlmLabTelemetry.Tags.ThinkingChars, thinkingChars);
            activity.SetTag(LlmLabTelemetry.Tags.SessionSaved, sessionSaved);
            if (thinkSeconds is { } ts) activity.SetTag(LlmLabTelemetry.Tags.ThinkingSeconds, ts);
            if (firstTokenSeconds is { } ft) activity.SetTag(LlmLabTelemetry.Tags.FirstTokenSeconds, ft);
            activity.SetTag("llmlab.chat.outcome", outcome);
            activity.SetStatus(outcome == "failed" ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        }

        var tags = new TagList
        {
            { "outcome", outcome },
            { "use_documents", cmd.UseDocuments },
            { "think", cmd.Think },
        };
        LlmLabTelemetry.ChatTurns.Add(1, tags);
        LlmLabTelemetry.ChatDuration.Record(seconds, tags);
        if (firstTokenSeconds is { } first) LlmLabTelemetry.FirstTokenLatency.Record(first, tags);
    }
}
