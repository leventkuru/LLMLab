using System.Diagnostics;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Application.Options;
using LlmLab.Application.Rag;
using LlmLab.Application.Telemetry;
using LlmLab.Domain.Common;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmLab.Application.Features.Documents;

public sealed record DocumentSummary(Guid Id, string FileName, long SizeBytes, int ChunkCount, DateTime CreatedAt);

public sealed record UploadDocumentCommand(string FileName, byte[] Content);

/// <summary>Belge yükleme hattı: metni çıkar -> parçala -> her parçayı göm -> belgeyi tek işlemde kaydet.</summary>
public sealed class UploadDocumentHandler(
    IDocumentRepository documents, ITextExtractor extractor, IEmbeddingService embeddings,
    IUnitOfWork uow, TimeProvider clock, IOptions<RagOptions> options, ILogger<UploadDocumentHandler> log)
{
    public async Task<DocumentSummary> HandleAsync(UploadDocumentCommand cmd, CancellationToken ct)
    {
        // İz ve metrikler: dosya adı/içeriği değil, yalnızca tür, boyut ve sayılar (kişisel veri sızmasın).
        using var activity = LlmLabTelemetry.ActivitySource.StartActivity("documents.upload");
        activity?.SetTag("llmlab.documents.extension", Path.GetExtension(cmd.FileName).ToLowerInvariant());
        activity?.SetTag("llmlab.documents.size_bytes", cmd.Content.Length);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await UploadAsync(cmd, activity, ct);
            LlmLabTelemetry.DocumentsUploaded.Add(1);
            LlmLabTelemetry.DocumentChunks.Record(result.ChunkCount);
            LlmLabTelemetry.UploadDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            throw;
        }
    }

    private async Task<DocumentSummary> UploadAsync(UploadDocumentCommand cmd, Activity? activity, CancellationToken ct)
    {
        var rag = options.Value;

        var ext = Path.GetExtension(cmd.FileName).ToLowerInvariant();
        if (!extractor.SupportedExtensions.Contains(ext))
            throw new ValidationException($"Desteklenen türler: {string.Join(", ", extractor.SupportedExtensions)}");
        if (cmd.Content.Length == 0 || cmd.Content.Length > rag.MaxFileBytes)
            throw new ValidationException($"Dosya boş olmamalı ve en fazla {rag.MaxFileBytes / 1024 / 1024} MB olmalı.");

        // 1) Metni çıkar
        string text;
        try
        {
            text = extractor.Extract(cmd.FileName, cmd.Content);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Metin çıkarılamadı: {File}", cmd.FileName);
            throw new UnprocessableException("Dosya okunamadı (bozuk ya da şifreli olabilir).");
        }

        // 2) Parçala
        var chunks = TextChunker.Split(text, rag.ChunkChars, rag.OverlapChars);
        activity?.SetTag("llmlab.documents.text_chars", text.Length);
        activity?.SetTag("llmlab.documents.chunks", chunks.Count);
        if (chunks.Count == 0)
            throw new UnprocessableException("Dosyadan metin çıkarılamadı. Taranmış (resim) bir PDF olabilir; bunun için OCR gerekir.");

        // 3) Her parçayı göm (gruplar halinde: her parça için ayrı istek atmaktan çok daha hızlı)
        var now = clock.GetUtcNow().UtcDateTime;
        var document = KnowledgeDocument.Create(cmd.FileName, cmd.Content.Length, now);
        foreach (var batch in chunks.Chunk(rag.EmbedBatchSize))
        {
            // Her grup ayrı bir alt aralık: büyük bir PDF'te zamanın nerede geçtiği (ve ilerleme) iz görünümünde okunur.
            using var batchActivity = LlmLabTelemetry.ActivitySource.StartActivity("documents.embed_batch");
            batchActivity?.SetTag("llmlab.documents.batch_size", batch.Length);
            var vectors = await embeddings.EmbedAsync(batch.Select(c => rag.DocumentPrefix + c).ToList(), ct);
            for (var i = 0; i < batch.Length; i++) document.AddChunk(batch[i], vectors[i]);
        }

        // 4) Kaydet: belge ve tüm parçaları tek işlemde, yarım belge kalmaz
        documents.Add(document);
        await uow.SaveChangesAsync(ct);

        log.LogInformation("Belge yüklendi: {File}, {Chunks} parça", document.FileName, document.Chunks.Count);
        return new DocumentSummary(document.Id, document.FileName, document.SizeBytes, document.Chunks.Count, document.CreatedAt);
    }
}

public sealed class ListDocumentsHandler(IDocumentRepository documents)
{
    public Task<IReadOnlyList<DocumentSummary>> HandleAsync(CancellationToken ct) => documents.ListAsync(ct);
}

public sealed class DeleteDocumentHandler(IDocumentRepository documents)
{
    public async Task HandleAsync(Guid id, CancellationToken ct)
    {
        if (!await documents.DeleteAsync(id, ct)) throw new NotFoundException("Belge bulunamadı.");
    }
}

public sealed record SearchDocumentsQuery(string Query, int? TopK);

public sealed class SearchDocumentsHandler(IDocumentSearch search)
{
    public async Task<IReadOnlyList<SourceReference>> HandleAsync(SearchDocumentsQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Query)) throw new ValidationException("Sorgu boş olamaz.");
        return await search.SearchAsync(query.Query, Math.Clamp(query.TopK ?? 5, 1, 20), ct);
    }
}
