using LlmLab.Application.Abstractions;
using LlmLab.Domain.Agents;
using LlmLab.Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmLab.Mcp.Tests;

/// <summary>
/// Güvence: alt ajan kodu yazıp ÇALIŞTIRMADAN bitirirse bir kez "çalıştır" hatırlatması yapılır; böylece koordinatör
/// çıktısı olmayan koddan sonuç uydurmaz.
/// </summary>
public class SubAgentCodeGuardTests
{
    private sealed class Agents(AgentDefinition agent) : IAgentRepository
    {
        public Task<AgentDefinition?> GetAsync(string id, CancellationToken ct) =>
            Task.FromResult<AgentDefinition?>(id == agent.Id ? agent : null);
        public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentDefinition>>([agent]);
        public void Add(AgentDefinition a) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Scripted(Func<AgentRequest, int, AgentEvent[]> script) : IChatAgent
    {
        public List<AgentRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            foreach (var e in script(request, Requests.Count))
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private static (InMemorySubAgentRunner Runner, Scripted Chat) Build(string[] allowedTools, Func<AgentRequest, int, AgentEvent[]> script)
    {
        var agent = AgentDefinition.Create("coder", "Kodcu", "", "", allowedTools, false, AgentHosting.Local, true, DateTime.UtcNow);
        var chat = new Scripted(script);
        var services = new ServiceCollection()
            .AddSingleton<IAgentRepository>(new Agents(agent))
            .AddSingleton<IChatAgent>(chat)
            .BuildServiceProvider();
        var runner = new InMemorySubAgentRunner(
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SubAgentOptions()),
            TimeProvider.System, NullLogger<InMemorySubAgentRunner>.Instance);
        return (runner, chat);
    }

    private const string CodeOnly = "İşte kod:\n```python\nprint(sum(range(10)))\n```";

    [Fact]
    public async Task Code_listed_but_not_executed_triggers_one_run_it_reminder_and_the_second_answer_wins()
    {
        var (runner, chat) = Build(["run_python"], (req, n) => n == 1
            ? [new TextDelta(CodeOnly), new SessionSaved("durum-1")]
            : [new ToolInvoked("run_python", "code=..."), new TextDelta("Çıktı: 45"), new SessionSaved("durum-2")]);

        var started = await runner.StartAsync("coder", "toplamı hesapla", default);
        var done = await runner.WaitAsync(started.TaskId, TimeSpan.FromSeconds(5), default);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains("ÇALIŞTIRMADIN", chat.Requests[1].Message);
        Assert.Equal("durum-1", chat.Requests[1].SessionState);   // ikinci tur aynı oturumda sürer
        Assert.Equal("Çıktı: 45", done!.Result);
        Assert.Contains("run_python", done.ToolCalls);
    }

    [Fact]
    public async Task No_reminder_when_the_tool_was_actually_called()
    {
        var (runner, chat) = Build(["run_python"], (_, _) =>
            [new ToolInvoked("run_python", "code=..."), new TextDelta("Çıktı: 45\n```\n45\n```")]);

        var started = await runner.StartAsync("coder", "x", default);
        await runner.WaitAsync(started.TaskId, TimeSpan.FromSeconds(5), default);

        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task No_reminder_for_agents_that_cannot_run_code_or_when_there_is_no_code()
    {
        var (docsOnly, docsChat) = Build(["search_documents"], (_, _) => [new TextDelta(CodeOnly)]);
        var a = await docsOnly.StartAsync("coder", "x", default);
        await docsOnly.WaitAsync(a.TaskId, TimeSpan.FromSeconds(5), default);
        Assert.Single(docsChat.Requests);

        var (plain, plainChat) = Build(["run_python"], (_, _) => [new TextDelta("Sonuç 45.")]);
        var b = await plain.StartAsync("coder", "x", default);
        await plain.WaitAsync(b.TaskId, TimeSpan.FromSeconds(5), default);
        Assert.Single(plainChat.Requests);
    }

    [Fact]
    public async Task The_reminder_is_sent_at_most_once()
    {
        var (runner, chat) = Build(["run_python"], (_, _) => [new TextDelta(CodeOnly)]); // hep kod, hiç çalıştırmıyor

        var started = await runner.StartAsync("coder", "x", default);
        var done = await runner.WaitAsync(started.TaskId, TimeSpan.FromSeconds(5), default);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(SubAgentStatus.Completed, done!.Status);
    }
}
