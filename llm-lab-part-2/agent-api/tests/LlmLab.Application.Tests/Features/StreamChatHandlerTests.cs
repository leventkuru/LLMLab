using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Application.Features.Chat;
using LlmLab.Domain.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmLab.Application.Tests.Features;

public class StreamChatHandlerTests
{
    private static readonly SourceReference Source = new(7, Guid.NewGuid(), "izin.md", 2, "Yıllık izin 14 gündür.", 0.61, 1);

    private readonly FakeConversationRepository _repo = new();
    private readonly FakeUnitOfWork _uow = new();

    private StreamChatHandler Handler(ScriptedAgent agent) =>
        new(_repo, new FakeAgentRepository(), agent, _uow, new FixedTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<StreamChatHandler>.Instance);

    private static async Task<List<ChatStreamEvent>> Drain(IAsyncEnumerable<ChatStreamEvent> stream)
    {
        var list = new List<ChatStreamEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    [Fact]
    public async Task New_conversation_saves_the_user_message_before_the_agent_runs_and_announces_its_id_first()
    {
        // Ajan çalıştığı ANDAKİ durumu kopyala (nesne referansı sonradan eklenen cevabı da görür).
        string[]? messagesWhenAgentRan = null;
        var agent = new ScriptedAgent(_ =>
        {
            // Ajan çalışırken kullanıcı mesajı zaten kaydedilmiş olmalı (model yanıt vermese de soru kaybolmasın).
            messagesWhenAgentRan = _repo.Store.Values.Single().Messages.Select(m => m.Content).ToArray();
            return ScriptedAgent.Events(new TextDelta("Merhaba"), new SessionSaved("{}"));
        });

        var events = await Drain(Handler(agent).HandleAsync(new StreamChatCommand("Selam", null, false, false), default));

        var started = Assert.IsType<ConversationStarted>(events[0]);
        Assert.Equal(_repo.Store.Keys.Single(), started.ConversationId);
        Assert.NotNull(messagesWhenAgentRan); // ajan gerçekten çalıştı
        Assert.Equal(["Selam"], messagesWhenAgentRan);
    }

    [Fact]
    public async Task Happy_path_persists_transcript_sources_thinking_and_the_agent_session()
    {
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(
            new ThinkingDelta("düşünüyorum"),
            new ToolInvoked("search_documents", "query=izin"),
            new SourcesFound([Source]),
            new TextDelta("14 gün "), new TextDelta("[1]"),
            new SessionSaved("""{"state":1}""")));

        var events = await Drain(Handler(agent).HandleAsync(new StreamChatCommand("İzin kaç gün?", null, true, true), default));

        // Dış dünyaya giden olaylar
        Assert.Contains(events, e => e is ToolCalled { Name: "search_documents" });
        Assert.Contains(events, e => e is SourcesAvailable s && s.Sources.Single() == Source);
        Assert.Equal("14 gün [1]", string.Concat(events.OfType<TokenChunk>().Select(t => t.Text)));

        // Kalıcı durum
        var conv = _repo.Store.Values.Single();
        Assert.Equal(2, conv.Messages.Count);
        var answer = conv.Messages[1];
        Assert.Equal(MessageRole.Assistant, answer.Role);
        Assert.Equal("14 gün [1]", answer.Content);
        Assert.Equal("düşünüyorum", answer.Thinking);
        Assert.NotNull(answer.ThinkSeconds);
        Assert.Equal(Source, Assert.Single(answer.Sources!));
        Assert.Equal("""{"state":1}""", conv.AgentSessionState);
    }

    [Fact]
    public async Task Tool_calls_are_saved_with_their_position_in_the_answer_text()
    {
        // Arayüz araç satırını metnin ARASINA koyar: "ön cümle → [araç] → asıl cevap". Konum bu yüzden saklanır.
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(
            new TextDelta("Arıyorum. "),
            new ToolInvoked("search_documents", "query=izin"),
            new SourcesFound([Source]),
            new TextDelta("14 gün."),
            new SessionSaved("{}")));

        await Drain(Handler(agent).HandleAsync(new StreamChatCommand("İzin?", null, false, true), default));

        var call = Assert.Single(_repo.Store.Values.Single().Messages[1].ToolCalls!);
        Assert.Equal("search_documents", call.Name);
        Assert.Equal("query=izin", call.Arguments);
        Assert.Equal("Arıyorum. ".Length, call.Position);
    }

    [Fact]
    public async Task A_turn_without_tools_has_an_empty_tool_list_not_null_sources()
    {
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(new TextDelta("Merhaba"), new SessionSaved("{}")));

        await Drain(Handler(agent).HandleAsync(new StreamChatCommand("Selam", null, false, true), default));

        var answer = _repo.Store.Values.Single().Messages[1];
        Assert.Empty(answer.ToolCalls!);
        Assert.Null(answer.Sources);
    }

    [Fact]
    public async Task Second_turn_hands_the_saved_session_back_to_the_agent()
    {
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(new TextDelta("tamam"), new SessionSaved("""{"turn":1}""")));
        var handler = Handler(agent);

        var first = await Drain(handler.HandleAsync(new StreamChatCommand("bir", null, false, false), default));
        var id = ((ConversationStarted)first[0]).ConversationId;
        await Drain(handler.HandleAsync(new StreamChatCommand("iki", id, false, false), default));

        Assert.Null(agent.Requests[0].SessionState);
        Assert.Equal("""{"turn":1}""", agent.Requests[1].SessionState);
    }

    [Fact]
    public async Task No_search_means_null_sources_but_an_empty_search_means_an_empty_list()
    {
        var noSearch = new ScriptedAgent(_ => ScriptedAgent.Events(new TextDelta("sohbet"), new SessionSaved("{}")));
        await Drain(Handler(noSearch).HandleAsync(new StreamChatCommand("a", null, false, true), default));
        Assert.Null(_repo.Store.Values.Single().Messages[1].Sources); // aranmadı: arayüz kaynak kutusu göstermez

        _repo.Store.Clear();
        var emptySearch = new ScriptedAgent(_ => ScriptedAgent.Events(new SourcesFound([]), new TextDelta("yok"), new SessionSaved("{}")));
        await Drain(Handler(emptySearch).HandleAsync(new StreamChatCommand("b", null, false, true), default));
        Assert.Empty(_repo.Store.Values.Single().Messages[1].Sources!); // arandı, bulunamadı: arayüz bunu söyler
    }

    [Fact]
    public async Task A_failure_midway_keeps_the_partial_answer_but_not_the_session()
    {
        // Bilinçli ödünleşim: yarım tur transkriptte görünür ama ajanın belleğine girmez.
        var agent = new ScriptedAgent(_ => Broken());
        static async IAsyncEnumerable<AgentEvent> Broken()
        {
            yield return new TextDelta("yarım cev");
            await Task.Yield();
            throw new InvalidOperationException("Ollama koptu");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Drain(Handler(agent).HandleAsync(new StreamChatCommand("soru", null, false, false), default)));

        var conv = _repo.Store.Values.Single();
        Assert.Equal("yarım cev", conv.Messages[1].Content);
        Assert.Null(conv.AgentSessionState);
    }

    [Fact]
    public async Task Unknown_conversation_is_not_found()
    {
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events());
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Drain(Handler(agent).HandleAsync(new StreamChatCommand("x", Guid.NewGuid(), false, false), default)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_message_is_rejected_before_anything_is_created(string message)
    {
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events());
        await Assert.ThrowsAsync<ValidationException>(() =>
            Drain(Handler(agent).HandleAsync(new StreamChatCommand(message, null, false, false), default)));
        Assert.Empty(_repo.Store);
        Assert.Empty(agent.Requests);
    }
}
