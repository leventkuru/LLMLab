using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Conversations;
using LlmLab.Application.Features.Documents;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;

namespace LlmLab.Application.Tests;

/// <summary>Testler için bellek içi sahte bağımlılıklar: Ollama ve veritabanı olmadan Application katmanı sınanır.</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        Saves++;
        return Task.FromResult(0);
    }
}

internal sealed class FakeConversationRepository : IConversationRepository
{
    public Dictionary<Guid, Conversation> Store { get; } = [];

    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Store.GetValueOrDefault(id));
    public void Add(Conversation conversation) => Store[conversation.Id] = conversation;

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ConversationDetail?> GetDetailAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeDocumentRepository : IDocumentRepository
{
    public List<KnowledgeDocument> Added { get; } = [];

    public void Add(KnowledgeDocument document) => Added.Add(document);
    public Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Senaryoya göre önceden yazılmış olayları üreten sahte ajan; aldığı istekleri de kaydeder.</summary>
internal sealed class ScriptedAgent(Func<AgentRequest, IAsyncEnumerable<AgentEvent>> script) : IChatAgent
{
    public List<AgentRequest> Requests { get; } = [];

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return script(request);
    }

    public static async IAsyncEnumerable<AgentEvent> Events(params AgentEvent[] events)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
    }
}

internal sealed class FakeEmbeddingService : IEmbeddingService
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        Calls.Add(inputs);
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => new float[EmbeddingSpec.Dimensions]).ToList());
    }
}

internal sealed class FakeTextExtractor(Func<string, byte[], string> extract) : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".md", ".pdf"];
    public string Extract(string fileName, byte[] content) => extract(fileName, content);
}
