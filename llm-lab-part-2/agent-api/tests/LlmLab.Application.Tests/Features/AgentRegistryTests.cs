using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Application.Features.Agents;
using LlmLab.Application.Features.Chat;
using LlmLab.Application.Features.Dialogs;
using LlmLab.Domain.Agents;
using LlmLab.Domain.Common;
using LlmLab.Domain.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmLab.Application.Tests.Features;

public class AgentDefinitionTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static AgentDefinition Make(string id = "analyst", IEnumerable<string>? tools = null) =>
        AgentDefinition.Create(id, "Analist", "açıklama", "rol", tools ?? ["calculate"], false, AgentHosting.Local, true, Now);

    [Theory]
    [InlineData("a")]            // çok kısa
    [InlineData("Analist")]      // büyük harf
    [InlineData("1abc")]         // rakamla başlar
    [InlineData("has space")]
    [InlineData("")]
    public void Rejects_invalid_ids(string id)
    {
        Assert.Throws<DomainException>(() => Make(id));
    }

    [Fact]
    public void Normalizes_tool_names_and_removes_duplicates()
    {
        var agent = Make(tools: [" calculate ", "calculate", "run_python", ""]);

        Assert.Equal(["calculate", "run_python"], agent.AllowedTools);
    }

    [Fact]
    public void Rejects_invalid_tool_names_and_too_long_instructions()
    {
        Assert.Throws<DomainException>(() => Make(tools: ["bad name!"]));
        Assert.Throws<DomainException>(() =>
            AgentDefinition.Create("analyst", "A", "", new string('x', AgentDefinition.MaxInstructionsLength + 1),
                [], false, AgentHosting.Local, true, Now));
    }

    [Fact]
    public void Updates_bump_the_version()
    {
        var agent = Make();
        var v = agent.Version;

        agent.Update("Yeni ad", "", "yeni rol", [], true, AgentHosting.Remote, false, Now);

        Assert.Equal(v + 1, agent.Version);
        Assert.True(agent.CanDelegate);
        Assert.False(agent.Enabled);
        Assert.Equal(AgentHosting.Remote, agent.Hosting);
    }

    [Fact]
    public void Default_agents_are_valid_unique_and_include_a_coordinator()
    {
        var defaults = DefaultAgents.Create(Now);

        Assert.Equal(defaults.Count, defaults.Select(d => d.Id).Distinct().Count());
        Assert.Contains(defaults, d => d.Id == Conversation.DefaultAgentId);
        Assert.Single(defaults, d => d.CanDelegate);
        // Koordinatör araç izin listesinde alt ajan araçları yoktur: onlar CanDelegate ile gelir.
        Assert.DoesNotContain(defaults.Single(d => d.CanDelegate).AllowedTools, t => t.Contains("subagent"));
    }
}

public class AgentRegistryHandlerTests
{
    private readonly FakeAgentRepository _repo = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private SaveAgentHandler Save => new(_repo, _uow, _clock);

    [Fact]
    public async Task Save_creates_then_updates_the_same_agent()
    {
        var created = await Save.HandleAsync(
            new SaveAgentCommand("analyst", "Analist", "d", "rol", ["calculate"], false, AgentHosting.Local, true), default);
        var updated = await Save.HandleAsync(
            new SaveAgentCommand("analyst", "Analist 2", "d", "rol 2", [], false, AgentHosting.Local, true), default);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Analist 2", _repo.Store["analyst"].DisplayName);
        Assert.Equal(created.Version + 1, updated.Version);
        Assert.Equal(2, _uow.Saves);
    }

    [Fact]
    public async Task The_default_agent_cannot_be_disabled_deleted_or_saved_as_disabled()
    {
        await Save.HandleAsync(
            new SaveAgentCommand(Conversation.DefaultAgentId, "Asistan", "", "", [], false, AgentHosting.Local, Enabled: false), default);
        Assert.True(_repo.Store[Conversation.DefaultAgentId].Enabled);

        await Assert.ThrowsAsync<ValidationException>(() =>
            new SetAgentEnabledHandler(_repo, _uow, _clock).HandleAsync(Conversation.DefaultAgentId, false, default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            new DeleteAgentHandler(_repo).HandleAsync(Conversation.DefaultAgentId, default));
    }

    [Fact]
    public async Task Enabled_switch_persists_and_unknown_ids_are_404()
    {
        await Save.HandleAsync(
            new SaveAgentCommand("coder", "Kodcu", null, null, null, false, AgentHosting.Local, true), default);

        var view = await new SetAgentEnabledHandler(_repo, _uow, _clock).HandleAsync("coder", false, default);

        Assert.False(view.Enabled);
        await Assert.ThrowsAsync<NotFoundException>(() => new GetAgentHandler(_repo).HandleAsync("yok", default));
    }
}

public class AgentSelectionInChatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static AgentDefinition Agent(string id, bool enabled = true, bool canDelegate = false) =>
        AgentDefinition.Create(id, id.ToUpperInvariant(), "", $"{id} rolü", ["calculate"], canDelegate, AgentHosting.Local, enabled, Now.UtcDateTime);

    private static async Task<List<ChatStreamEvent>> Run(StreamChatHandler h, StreamChatCommand cmd)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in h.HandleAsync(cmd, default)) events.Add(e);
        return events;
    }

    private static (StreamChatHandler Handler, ScriptedAgent Agent, FakeConversationRepository Repo) Build(params AgentDefinition[] agents)
    {
        var repo = new FakeConversationRepository();
        var agent = new ScriptedAgent(_ => ScriptedAgent.Events(new TextDelta("tamam"), new SessionSaved("{}")));
        var handler = new StreamChatHandler(repo, new FakeAgentRepository(agents), agent, new FakeUnitOfWork(),
            new FixedTimeProvider(Now), NullLogger<StreamChatHandler>.Instance);
        return (handler, agent, repo);
    }

    [Fact]
    public async Task A_new_conversation_uses_the_requested_agent_and_remembers_it()
    {
        var (handler, agent, repo) = Build(Agent("analyst"));

        await Run(handler, new StreamChatCommand("hesapla", null, false, false, "analyst"));

        Assert.Equal("analyst", agent.Requests.Single().Profile!.Id);
        Assert.Equal("analyst", repo.Store.Values.Single().AgentId);
    }

    [Fact]
    public async Task An_existing_conversation_keeps_its_own_agent_even_if_another_is_requested()
    {
        var (handler, agent, repo) = Build(Agent("analyst"), Agent("coder"));
        var first = await Run(handler, new StreamChatCommand("a", null, false, false, "analyst"));
        var id = first.OfType<ConversationStarted>().Single().ConversationId;

        await Run(handler, new StreamChatCommand("b", id, false, false, "coder"));

        Assert.Equal(["analyst", "analyst"], agent.Requests.Select(r => r.Profile!.Id));
    }

    [Fact]
    public async Task Disabled_and_unknown_agents_fail_before_anything_is_stored()
    {
        var (handler, _, repo) = Build(Agent("analyst", enabled: false));

        await Assert.ThrowsAsync<ValidationException>(() => Run(handler, new StreamChatCommand("x", null, false, false, "analyst")));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(handler, new StreamChatCommand("x", null, false, false, "yok")));
        Assert.Empty(repo.Store);
    }

    [Fact]
    public async Task Without_an_agent_id_the_built_in_default_is_used()
    {
        var (handler, agent, _) = Build();

        await Run(handler, new StreamChatCommand("merhaba", null, false, false));

        Assert.Equal(Conversation.DefaultAgentId, agent.Requests.Single().Profile!.Id);
    }
}

public class DialogHandlerTests
{
    private static AgentDefinition Agent(string id, bool canDelegate = false) =>
        AgentDefinition.Create(id, id.ToUpperInvariant(), "", "", [], canDelegate, AgentHosting.Local, true, DateTime.UtcNow);

    private static (DialogHandler Handler, ScriptedAgent Chat) Build(params AgentDefinition[] agents)
    {
        var chat = new ScriptedAgent(req => ScriptedAgent.Events(
            new TextDelta($"{req.Profile!.Id} cevap"), new SessionSaved($"durum-{req.Profile.Id}")));
        return (new DialogHandler(new FakeAgentRepository(agents), chat, NullLogger<DialogHandler>.Instance), chat);
    }

    private static async Task<List<DialogEvent>> Run(DialogHandler h, DialogCommand cmd)
    {
        var events = new List<DialogEvent>();
        await foreach (var e in h.HandleAsync(cmd, default)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task Agents_alternate_and_each_receives_the_other_ones_labelled_message()
    {
        var (handler, chat) = Build(Agent("alice"), Agent("bob"));

        var events = await Run(handler, new DialogCommand("alice", "bob", "yapay zekâ", Turns: 4));

        Assert.Equal(["alice", "bob", "alice", "bob"], chat.Requests.Select(r => r.Profile!.Id));
        Assert.Contains("Konu: yapay zekâ", chat.Requests[0].Message);
        Assert.Equal("[ALICE]: alice cevap", chat.Requests[1].Message); // B, A'nın cevabını etiketle alır
        Assert.Equal("[BOB]: bob cevap", chat.Requests[2].Message);
        Assert.IsType<DialogFinished>(events[^1]);
        Assert.Equal(4, events.OfType<DialogTurnCompleted>().Count());
    }

    [Fact]
    public async Task Each_agent_keeps_its_own_session_across_turns()
    {
        var (handler, chat) = Build(Agent("alice"), Agent("bob"));

        await Run(handler, new DialogCommand("alice", "bob", "konu", Turns: 4));

        Assert.Null(chat.Requests[0].SessionState);
        Assert.Equal("durum-alice", chat.Requests[2].SessionState);
        Assert.Equal("durum-bob", chat.Requests[3].SessionState);
    }

    [Fact]
    public async Task Turn_count_is_clamped_and_dialog_rules_are_added()
    {
        var (handler, chat) = Build(Agent("alice"), Agent("bob"));

        var events = await Run(handler, new DialogCommand("alice", "bob", "konu", Turns: 999));

        Assert.Equal(DialogHandler.MaxTurns, events.OfType<DialogTurnCompleted>().Count());
        Assert.All(chat.Requests, r => Assert.Contains("en fazla 3 cümle", r.ExtraInstructions));
    }

    [Fact]
    public async Task Same_agent_coordinators_and_disabled_agents_are_rejected()
    {
        var (handler, _) = Build(Agent("alice"), Agent("coord", canDelegate: true));

        await Assert.ThrowsAsync<ValidationException>(() => Run(handler, new DialogCommand("alice", "alice", "x")));
        await Assert.ThrowsAsync<ValidationException>(() => Run(handler, new DialogCommand("alice", "coord", "x")));
        await Assert.ThrowsAsync<NotFoundException>(() => Run(handler, new DialogCommand("alice", "yok", "x")));
    }

    [Fact]
    public async Task An_empty_answer_ends_the_dialog_early()
    {
        var chat = new ScriptedAgent(_ => ScriptedAgent.Events(new SessionSaved("{}")));
        var handler = new DialogHandler(
            new FakeAgentRepository(Agent("alice"), Agent("bob")), chat, NullLogger<DialogHandler>.Instance);

        var events = await Run(handler, new DialogCommand("alice", "bob", "konu", Turns: 6));

        Assert.Single(chat.Requests);
        Assert.Equal(1, ((DialogFinished)events[^1]).Turns);
    }
}
