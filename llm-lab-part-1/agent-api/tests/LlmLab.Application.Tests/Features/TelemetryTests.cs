using System.Diagnostics;
using System.Runtime.CompilerServices;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Chat;
using LlmLab.Application.Telemetry;
using LlmLab.Domain.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmLab.Application.Tests.Features;

/// <summary>
/// Gözlemlenebilirlik de davranıştır: bir tur başarılı, hatalı ya da iptal bittiğinde iz doğru sonucu taşımalıdır.
/// Gerçek bir toplayıcı gerekmez; .NET'in kendi ActivityListener'ı yeterlidir. Testler paralel çalıştığı için
/// iz, kendi sohbet kimliğiyle süzülür.
/// </summary>
public class TelemetryTests
{
    private readonly FakeConversationRepository _repo = new();

    private static ActivityListener Listen(List<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LlmLabTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (sink) sink.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private StreamChatHandler Handler(IChatAgent agent) =>
        new(_repo, agent, new FakeUnitOfWork(), new FixedTimeProvider(DateTimeOffset.UnixEpoch), NullLogger<StreamChatHandler>.Instance);

    private static Activity TurnOf(List<Activity> sink, Guid conversationId)
    {
        lock (sink)
            return sink.Single(a => a.OperationName == "chat.turn" && a.GetTagItem(LlmLabTelemetry.Tags.ConversationId) is Guid g && g == conversationId);
    }

    private static async Task<Guid> Drain(IAsyncEnumerable<ChatStreamEvent> stream, bool swallowErrors = false)
    {
        Guid id = default;
        try
        {
            await foreach (var e in stream)
                if (e is ConversationStarted s) id = s.ConversationId;
        }
        catch when (swallowErrors) { /* hata sonucu iz etiketinden okunur */ }
        return id;
    }

    [Fact]
    public async Task A_successful_turn_records_outcome_counts_and_settings_but_never_the_message_text()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var source = new SourceReference(1, Guid.NewGuid(), "a.md", 0, "metin", 0.6);
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(
            new ToolInvoked("search_documents", "query=gizli soru"), new SourcesFound([source]),
            new TextDelta("cevap"), new SessionSaved("{}")));

        var id = await Drain(Handler(agent).HandleAsync(new StreamChatCommand("GİZLİ KULLANICI MESAJI", null, Think: true, UseDocuments: true), default));

        var turn = TurnOf(spans, id);
        Assert.Equal("ok", turn.GetTagItem("llmlab.chat.outcome"));
        Assert.Equal(1, turn.GetTagItem(LlmLabTelemetry.Tags.ToolCallCount));
        Assert.Equal(1, turn.GetTagItem(LlmLabTelemetry.Tags.Sources));
        Assert.Equal(5, turn.GetTagItem(LlmLabTelemetry.Tags.AnswerChars));
        Assert.Equal(true, turn.GetTagItem(LlmLabTelemetry.Tags.UseDocuments));
        Assert.Equal(true, turn.GetTagItem(LlmLabTelemetry.Tags.SessionSaved));
        Assert.NotNull(turn.GetTagItem(LlmLabTelemetry.Tags.FirstTokenSeconds));
        Assert.Equal(ActivityStatusCode.Ok, turn.Status);
        Assert.Contains(turn.Events, e => e.Name == "tool_called");

        // Gizlilik: kullanıcı mesajının metni hiçbir etikette yer almaz.
        Assert.DoesNotContain(turn.TagObjects, t => t.Value?.ToString()?.Contains("GİZLİ") == true);
    }

    [Fact]
    public async Task Sources_tag_is_minus_one_when_no_document_search_happened()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(new TextDelta("selam"), new SessionSaved("{}")));

        var id = await Drain(Handler(agent).HandleAsync(new StreamChatCommand("Merhaba", null, false, true), default));

        // -1: "aranmadı". Sıfır, "arandı ama bulunamadı" anlamına gelir; iki durum izde de ayrışır.
        Assert.Equal(-1, TurnOf(spans, id).GetTagItem(LlmLabTelemetry.Tags.Sources));
    }

    [Fact]
    public async Task A_model_failure_marks_the_span_as_failed()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        var agent = new ScriptedAgent(_ => Broken());
        static async IAsyncEnumerable<AgentEvent> Broken()
        {
            yield return new TextDelta("yarım");
            await Task.Yield();
            throw new InvalidOperationException("Ollama koptu");
        }

        var id = await Drain(Handler(agent).HandleAsync(new StreamChatCommand("soru", null, false, false), default), swallowErrors: true);

        var turn = TurnOf(spans, id);
        Assert.Equal("failed", turn.GetTagItem("llmlab.chat.outcome"));
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
    }

    [Fact]
    public async Task A_client_abort_is_recorded_as_canceled_not_as_an_error()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        using var cts = new CancellationTokenSource();
        var agent = new CancelAwareAgent();

        Guid id = default;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in Handler(agent).HandleAsync(new StreamChatCommand("soru", null, false, false), cts.Token))
            {
                if (e is ConversationStarted s) id = s.ConversationId;
                if (e is TokenChunk) cts.Cancel(); // kullanıcı "Durdur"a bastı
            }
        });

        var turn = TurnOf(spans, id);
        Assert.Equal("canceled", turn.GetTagItem("llmlab.chat.outcome"));
        Assert.NotEqual(ActivityStatusCode.Error, turn.Status); // Durdur bir hata değildir
    }

    private sealed class CancelAwareAgent : IChatAgent
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextDelta("ilk");
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDelta("ikinci");
        }
    }
}
