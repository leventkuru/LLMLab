using System.Diagnostics;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using LlmLab.Application.Rag;
using LlmLab.Application.Telemetry;
using LlmLab.Domain.Conversations;
using Microsoft.Extensions.Options;

namespace LlmLab.Application.Features.Documents;

/// <summary>
/// Hibrit belge araması.
///
/// Neden hibrit? Embedding "anlam" benzerliğini yakalar ama kısa ve genel sorgularda ("aurora nedir") zayıftır;
/// özel isimler ve nadir terimlerde düz kelime eşleşmesi çok daha güvenilirdir. İkisini birleştiriyoruz:
///   1) vektör araması: sorguya anlamca en yakın parçalar (cosine),
///   2) anahtar kelime araması: sorgudaki ayırt edici kelimeleri içeren parçalar.
/// Kelimeyle bulunan parçalara sıralamada küçük bir bonus verilir; sohbette daha düşük bir eşikle kabul edilirler.
/// </summary>
public sealed class HybridDocumentSearch(
    IEmbeddingService embeddings, IChunkSearcher chunks, IOptions<RagOptions> options,
    IOptions<TelemetryOptions> telemetry) : IDocumentSearch
{
    public async Task<IReadOnlyList<SourceReference>> SearchAsync(string query, int topK, CancellationToken ct)
    {
        using var activity = LlmLabTelemetry.ActivitySource.StartActivity("rag.search");
        activity?.SetTag("llmlab.rag.top_k", topK);
        activity?.SetTag("llmlab.rag.query_chars", query.Length);
        // Sorgunun kendisi kullanıcı verisidir: yalnızca açıkça izin verilmişse izlere yazılır.
        if (telemetry.Value.CaptureSensitiveData) activity?.SetTag("llmlab.rag.query", query);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var results = await SearchCoreAsync(query, topK, activity, ct);
            activity?.SetTag("llmlab.rag.results", results.Count);
            activity?.SetTag("llmlab.rag.best_score", results.Count > 0 ? results.Max(r => r.Score) : null);
            LlmLabTelemetry.SearchResults.Record(results.Count);
            return results;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            throw;
        }
        finally
        {
            LlmLabTelemetry.SearchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<IReadOnlyList<SourceReference>> SearchCoreAsync(
        string query, int topK, Activity? activity, CancellationToken ct)
    {
        var rag = options.Value;

        // Sorguyu, belge parçalarıyla aynı modelle ve aynı uzaya gömüyoruz.
        var queryVector = (await embeddings.EmbedAsync([rag.QueryPrefix + query.Trim()], ct))[0];

        // 1) Vektör
        var rows = (await chunks.NearestAsync(queryVector, topK, ct)).ToDictionary(r => r.ChunkId);

        // 2) Anahtar kelime: her ayırt edici kelime için o kelimeyi içeren parçalar
        var hits = new Dictionary<long, int>(); // parça -> eşleşen kelime sayısı
        var tokens = QueryTokenizer.Tokenize(query);
        activity?.SetTag("llmlab.rag.keyword_tokens", tokens.Count);
        if (tokens.Count > 0)
        {
            // Çok parçada geçen kelime ayırt edici değildir (basit IDF mantığı).
            var total = await chunks.CountAsync(ct);
            var maxMatches = Math.Max(3, (int)(total * 0.3));

            foreach (var token in tokens)
            {
                var matched = await chunks.ContainingAsync(token, queryVector, maxMatches + 1, ct);
                if (matched.Count > maxMatches)
                {
                    // Çok yaygın kelime (ör. belgenin kendi adı): yeni aday eklemez, çünkü neredeyse her parçayı getirirdi.
                    // Ama vektör aramasının zaten bulduğu adaylardan kelimeyi içerenleri yine de destekler.
                    foreach (var r in rows.Values.ToList())
                    {
                        if (r.Text.Contains(token, StringComparison.CurrentCultureIgnoreCase))
                            hits[r.ChunkId] = hits.GetValueOrDefault(r.ChunkId) + 1;
                    }
                    continue;
                }

                foreach (var r in matched)
                {
                    hits[r.ChunkId] = hits.GetValueOrDefault(r.ChunkId) + 1;
                    rows.TryAdd(r.ChunkId, r);
                }
            }
        }

        activity?.SetTag("llmlab.rag.keyword_matched_chunks", hits.Count);

        // 3) Birleştir: benzerlik + (kelime eşleşmesi başına küçük bonus, en fazla 3). Gösterilen puan saf cosine'dir.
        return rows.Values
            .Select(r => (Row: r, Keyword: hits.GetValueOrDefault(r.ChunkId)))
            .OrderByDescending(x => (1 - x.Row.Distance) + rag.KeywordBoost * Math.Min(x.Keyword, 3))
            .Take(topK)
            .Select(x => new SourceReference(
                x.Row.ChunkId, x.Row.DocumentId, x.Row.FileName, x.Row.Ordinal, x.Row.Text,
                Math.Round(1 - x.Row.Distance, 4), x.Keyword))
            .ToList();
    }
}
