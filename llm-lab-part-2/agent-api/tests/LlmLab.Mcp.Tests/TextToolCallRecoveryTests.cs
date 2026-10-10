using System.Text.Json;
using LlmLab.Infrastructure.Ai;
using Microsoft.Extensions.AI;

namespace LlmLab.Mcp.Tests;

/// <summary>
/// qwen3:8b ile gözlenen hata: araç çağrısı yapılandırılmış çağrı yerine düz metin olarak yazılıyor ve araç hiç
/// çalışmıyor. Bu testler dönüştürmenin muhafazakâr olduğunu da korur: gerçek bir cevaba asla dokunmaz.
/// </summary>
public class TextToolCallRecoveryTests
{
    private static readonly string[] Tools = ["run_python", "calculate", "get_current_time"];

    private sealed class FakeStream(params string[] chunks) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var c in chunks)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, c);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatOptions WithTools() => new()
    {
        Tools = [.. Tools.Select(n => (AITool)AIFunctionFactory.Create(() => "x", n))],
    };

    private static async Task<List<ChatResponseUpdate>> Stream(IChatClient inner, ChatOptions? options)
    {
        var client = new TextToolCallRecoveryClient(inner);
        var list = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")], options)) list.Add(u);
        return list;
    }

    // ---- Ayrıştırıcı ----

    [Fact]
    public void Parses_name_followed_by_json_arguments()
    {
        Assert.True(TextToolCallParser.TryParse("run_python {\"code\": \"print(1)\", \"timeoutSeconds\": 10}", Tools, out var call));

        Assert.Equal("run_python", call!.Name);
        Assert.Equal("print(1)", ((JsonElement)call.Arguments!["code"]!).GetString());
        Assert.Equal(10, ((JsonElement)call.Arguments["timeoutSeconds"]!).GetInt32());
    }

    [Theory]
    [InlineData("{\"name\": \"calculate\", \"arguments\": {\"expression\": \"1+1\"}}")]
    [InlineData("calculate({\"expression\": \"1+1\"})")]
    [InlineData("calculate: {\"expression\": \"1+1\"}")]
    [InlineData("```json\n{\"name\": \"calculate\", \"parameters\": {\"expression\": \"1+1\"}}\n```")]
    [InlineData("  calculate\n{\"expression\": \"1+1\",}  ")] // sondaki virgül hoşgörülür
    public void Parses_the_common_textual_forms(string text)
    {
        Assert.True(TextToolCallParser.TryParse(text, Tools, out var call));
        Assert.Equal("calculate", call!.Name);
        Assert.Equal("1+1", ((JsonElement)call.Arguments!["expression"]!).GetString());
    }

    [Theory]
    [InlineData("run_python aracını kullanarak hesapladım.")]                    // düz cümle
    [InlineData("Sonuç: {\"toplam\": 55}")]                                       // JSON var ama araç çağrısı değil
    [InlineData("bilinmeyen_arac {\"x\": 1}")]                                    // bilinen araç değil
    [InlineData("{\"name\": \"bilinmeyen\", \"arguments\": {}}")]
    [InlineData("calculate {\"expression\": \"1+1\"} sonucu 2 dir.")]             // çağrıdan sonra metin var
    [InlineData("run_python {\"code\": \"print(1)\"")]                            // bozuk JSON
    [InlineData("calculate [1, 2]")]                                              // argüman nesne değil
    [InlineData("")]
    public void Leaves_ordinary_text_alone(string text)
    {
        Assert.False(TextToolCallParser.TryParse(text, Tools, out var call));
        Assert.Null(call);
    }

    // ---- Akış sarmalayıcısı ----

    [Fact]
    public async Task A_streamed_textual_call_becomes_one_real_function_call()
    {
        var updates = await Stream(new FakeStream("run_py", "thon {\"code\"", ": \"print(2)\"}"), WithTools());

        var call = Assert.Single(updates).Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("run_python", call.Name);
        Assert.Equal("print(2)", ((JsonElement)call.Arguments!["code"]!).GetString());
    }

    [Fact]
    public async Task Ordinary_answers_stream_through_unchanged_and_unbuffered()
    {
        var chunks = new[] { "Merhaba! ", "Nasıl ", "yardımcı ", "olabilirim?" };

        var updates = await Stream(new FakeStream(chunks), WithTools());

        Assert.Equal(chunks, updates.Select(u => u.Text));
    }

    [Fact]
    public async Task Text_that_merely_starts_like_a_tool_is_returned_intact_after_buffering()
    {
        var updates = await Stream(new FakeStream("calculate ", "aracı 5 sonucunu ", "verdi."), WithTools());

        Assert.Equal("calculate aracı 5 sonucunu verdi.", string.Concat(updates.Select(u => u.Text)));
        Assert.DoesNotContain(updates, u => u.Contents.OfType<FunctionCallContent>().Any());
    }

    [Fact]
    public async Task Without_tools_nothing_is_touched()
    {
        var updates = await Stream(new FakeStream("run_python {\"code\": \"1\"}"), options: null);

        Assert.Equal("run_python {\"code\": \"1\"}", Assert.Single(updates).Text);
    }

    [Fact]
    public async Task The_non_streaming_path_recovers_textual_calls_too()
    {
        var client = new TextToolCallRecoveryClient(new FakeStream("{\"name\": \"get_current_time\", \"arguments\": {}}"));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "saat?")], WithTools());

        var call = response.Messages.Single().Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("get_current_time", call.Name);
    }
}
