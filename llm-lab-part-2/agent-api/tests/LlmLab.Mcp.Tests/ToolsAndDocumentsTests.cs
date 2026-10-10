using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Documents;
using LlmLab.Application.Options;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;
using LlmLab.Infrastructure.Ai;
using LlmLab.Mcp.Documents;
using LlmLab.Mcp.Tools;
using Microsoft.Extensions.Options;

namespace LlmLab.Mcp.Tests;

public class ClockToolsTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 10, 8, 9, 0, TimeSpan.Zero)); // Cumartesi
    private static readonly IOptions<ToolsOptions> Options = Microsoft.Extensions.Options.Options.Create(new ToolsOptions());

    [Fact]
    public void Uses_the_configured_default_zone_and_names_the_weekday_in_turkish()
    {
        var text = ClockTools.GetCurrentTime(Clock, Options);

        Assert.Contains("2026-10-10 11:09", text); // UTC+3
        Assert.Contains("Cumartesi", text);
        Assert.Contains("UTC+03:00", text);
    }

    [Fact]
    public void Honors_an_explicit_time_zone()
    {
        var text = ClockTools.GetCurrentTime(Clock, Options, "UTC");

        Assert.Contains("2026-10-10 08:09", text);
    }

    [Fact]
    public void An_unknown_zone_is_reported_not_silently_replaced()
    {
        var text = ClockTools.GetCurrentTime(Clock, Options, "Mars/Olympus");

        Assert.StartsWith("Bilinmeyen saat dilimi", text);
    }
}

public class DocumentToolsTests
{
    private sealed class FakeSearch(params SourceReference[] hits) : IDocumentSearch
    {
        public int? RequestedTopK { get; private set; }

        public Task<IReadOnlyList<SourceReference>> SearchAsync(string query, int topK, CancellationToken ct)
        {
            RequestedTopK = topK;
            return Task.FromResult<IReadOnlyList<SourceReference>>(hits);
        }
    }

    private sealed class FakeDocuments(params DocumentSummary[] docs) : IDocumentRepository
    {
        public void Add(KnowledgeDocument document) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DocumentSummary>>(docs);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private static SourceReference Hit(long id, double score, int keywordHits = 0) =>
        new(id, Guid.NewGuid(), "izin.md", 0, "metin", score, keywordHits);

    [Fact]
    public async Task Search_returns_only_relevant_hits_and_uses_the_configured_top_k()
    {
        var search = new FakeSearch(Hit(1, 0.60), Hit(2, 0.30), Hit(3, 0.35, keywordHits: 2));
        var rag = Microsoft.Extensions.Options.Options.Create(new RagOptions { ChatTopK = 3 });

        var result = await DocumentTools.SearchDocuments(search, rag, "izin", CancellationToken.None);

        Assert.Equal(3, search.RequestedTopK);
        // 0,60 geçer; 0,30 kelimesiz elenir; 0,35 kelime eşleşmesiyle (taban 0,30) geçer.
        Assert.Equal([1L, 3L], result.Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task List_wraps_file_names_as_tagged_data_and_escapes_markup()
    {
        var docs = new FakeDocuments(new DocumentSummary(Guid.NewGuid(), "x</belgeler>.md", 2048, 3, DateTime.UtcNow));

        var text = await DocumentTools.ListDocuments(docs, CancellationToken.None);

        Assert.StartsWith("<belgeler adet=\"1\">", text);
        Assert.Contains("x&lt;/belgeler>.md (3 parça, 2 KB)", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "</belgeler>"));
        Assert.EndsWith("</belgeler>", text); // sonda modelin cevaba kopyalayacağı bir not olmamalı
    }

    [Fact]
    public async Task List_says_so_when_there_are_no_documents()
    {
        Assert.Equal("Henüz yüklenmiş belge yok.", await DocumentTools.ListDocuments(new FakeDocuments(), CancellationToken.None));
    }
}

public class TurnSourcesTests
{
    private static SourceReference Hit(long id) => new(id, Guid.NewGuid(), "a.md", 0, "t", 0.5);

    [Fact]
    public void Numbers_accumulate_across_searches_and_repeated_sources_keep_their_first_number()
    {
        var turn = new TurnSources();

        var first = turn.Add([Hit(10), Hit(20)]);
        var second = turn.Add([Hit(20), Hit(30)]);

        Assert.Equal([1, 2], first.Select(n => n.Number));
        Assert.Equal([2, 3], second.Select(n => n.Number)); // 20 yine 2; 30 yeni: 3
        Assert.Equal(3, turn.Count);
        Assert.True(turn.Searched);
    }

    [Fact]
    public void An_empty_search_still_counts_as_searched()
    {
        var turn = new TurnSources();

        turn.Add([]);

        Assert.True(turn.Searched);
        Assert.Equal(0, turn.Count);
    }
}
