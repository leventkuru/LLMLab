using LlmLab.Application.Abstractions;
using LlmLab.Domain.Agents;
using LlmLab.Infrastructure.Agents;
using Microsoft.Extensions.AI;

namespace LlmLab.Mcp.Tests;

/// <summary>
/// Koordinatör güvencesinin parçaları: hangi görevlerin sonucu koordinatöre verildi, hangileri hâlâ "bekliyor".
/// (Metni atıp sonuçları sistemin vermesi MafChatAgent'ta; burada onun dayandığı kayıt ve toplama sınanır.)
/// </summary>
public class SubAgentGuardTests
{
    private sealed class FakeRunner : ISubAgentRunner
    {
        public Dictionary<string, SubAgentSnapshot> Tasks { get; } = [];
        public List<string> Stopped { get; } = [];
        private int _next;

        public Task<SubAgentSnapshot> StartAsync(string agentId, string task, CancellationToken ct)
        {
            var snap = Snap($"t{++_next}", agentId, SubAgentStatus.Running);
            Tasks[snap.TaskId] = snap;
            return Task.FromResult(snap);
        }

        public SubAgentSnapshot? Get(string taskId) => Tasks.GetValueOrDefault(taskId);

        public Task<SubAgentSnapshot?> WaitAsync(string taskId, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(Tasks.GetValueOrDefault(taskId));

        public bool Stop(string taskId)
        {
            Stopped.Add(taskId);
            return Tasks.ContainsKey(taskId);
        }

        public IReadOnlyList<SubAgentSnapshot> List() => Tasks.Values.ToList();

        public static SubAgentSnapshot Snap(string id, string agent, SubAgentStatus status, string? result = null) =>
            new(id, agent, "görev", status, result, null, DateTime.UtcNow, null, []);
    }

    private sealed class NoAgents : IAgentRepository
    {
        public Task<AgentDefinition?> GetAsync(string id, CancellationToken ct) => Task.FromResult<AgentDefinition?>(null);
        public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([]);
        public void Add(AgentDefinition agent) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private static AIFunction Fn(IReadOnlyList<AITool> tools, string name) => tools.OfType<AIFunction>().Single(t => t.Name == name);

    /// <summary>AIFunction metin sonucu JsonElement döndürür; testler düz metinle çalışsın.</summary>
    private static async Task<string> Call(AIFunction f, params (string Key, object? Value)[] args)
    {
        var a = new AIFunctionArguments();
        foreach (var (k, v) in args) a[k] = v;
        return (await f.InvokeAsync(a))?.ToString() ?? "";
    }

    [Fact]
    public void The_delegation_rules_name_the_real_sub_agent_tools()
    {
        // Talimat metni Application'da, araç adları Infrastructure'da: sürüklenmesin diye burada birlikte sınanır.
        var prompt = LlmLab.Application.Rag.AgentPrompts.Compose(new LlmLab.Application.Rag.AgentCapabilities(
            true, false, SubAgentTools.ToolNames, CanDelegate: true));

        foreach (var name in SubAgentTools.ToolNames.Where(n => n != SubAgentTools.StopName))
            Assert.Contains($"`{name}`", prompt);
    }
    [Fact]
    public async Task A_started_task_stays_pending_until_its_result_is_actually_fetched()
    {
        var runner = new FakeRunner();
        var turn = new SubAgentTurn();
        var tools = new SubAgentTools(runner, new NoAgents()).Create(turn);

        var started = await Call(Fn(tools, SubAgentTools.StartName), ("agent", "worker"), ("task", "x"));
        Assert.Contains("SONUCU HENÜZ YOK", started);
        Assert.Equal(["t1"], turn.Pending());

        // Hâlâ çalışıyorsa sonuç verilmiş sayılmaz.
        await Call(Fn(tools, SubAgentTools.GetName), ("taskId", "t1"), ("waitSeconds", 0));
        Assert.Equal(["t1"], turn.Pending());

        // Bittikten sonra alındığında bekleyen kalmaz.
        runner.Tasks["t1"] = FakeRunner.Snap("t1", "worker", SubAgentStatus.Completed, "cevap");
        var result = await Call(Fn(tools, SubAgentTools.GetName), ("taskId", "t1"), ("waitSeconds", 0));
        Assert.Contains("cevap", result);
        Assert.Empty(turn.Pending());
    }

    [Fact]
    public async Task A_stopped_task_is_not_pending()
    {
        var runner = new FakeRunner();
        var turn = new SubAgentTurn();
        var tools = new SubAgentTools(runner, new NoAgents()).Create(turn);

        await Call(Fn(tools, SubAgentTools.StartName), ("agent", "worker"), ("task", "x"));
        await Call(Fn(tools, SubAgentTools.StopName), ("taskId", "t1"));

        Assert.Empty(turn.Pending());
    }

    [Fact]
    public async Task Collect_waits_for_every_pending_task_and_returns_tagged_results()
    {
        var runner = new FakeRunner();
        var turn = new SubAgentTurn();
        var tools = new SubAgentTools(runner, new NoAgents());
        var created = tools.Create(turn);

        await Call(Fn(created, SubAgentTools.StartName), ("agent", "a"), ("task", "x"));
        await Call(Fn(created, SubAgentTools.StartName), ("agent", "b"), ("task", "y"));
        runner.Tasks["t1"] = FakeRunner.Snap("t1", "a", SubAgentStatus.Completed, "birinci sonuç");
        runner.Tasks["t2"] = FakeRunner.Snap("t2", "b", SubAgentStatus.Failed);

        var (message, ids) = await tools.CollectAsync(turn.Pending(), TimeSpan.FromSeconds(1), default);

        Assert.Equal(["t1", "t2"], ids);
        Assert.Contains("<altajan_sonucu gorev=\"t1\"", message);
        Assert.Contains("birinci sonuç", message);
        Assert.Contains("durum: başarısız", message);
        Assert.Contains("uydurma", message);        // "sonuçlarda olmayanı uydurma" talimatı
        Assert.Empty(turn.Pending());               // toplandıktan sonra döngü oluşmaz
    }

    [Fact]
    public async Task Collect_marks_even_unfinished_tasks_as_collected_so_the_loop_terminates()
    {
        var runner = new FakeRunner();
        var turn = new SubAgentTurn();
        var tools = new SubAgentTools(runner, new NoAgents());
        await Call(Fn(tools.Create(turn), SubAgentTools.StartName), ("agent", "a"), ("task", "x"));

        var (message, _) = await tools.CollectAsync(turn.Pending(), TimeSpan.Zero, default); // t1 hâlâ Running

        Assert.Contains("çalışıyor", message);
        Assert.Empty(turn.Pending());
    }
}
