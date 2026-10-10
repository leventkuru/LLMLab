using LlmLab.Application.Common;
using LlmLab.Application.Features.Documents;
using LlmLab.Application.Options;
using LlmLab.Domain.Common;
using LlmLab.Domain.Conversations;
using LlmLab.Domain.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace LlmLab.Application.Tests.Features;

public class UploadDocumentHandlerTests
{
    private readonly FakeDocumentRepository _docs = new();
    private readonly FakeEmbeddingService _embeddings = new();
    private readonly FakeUnitOfWork _uow = new();

    private UploadDocumentHandler Handler(Func<string, byte[], string> extract, RagOptions? rag = null) =>
        new(_docs, new FakeTextExtractor(extract), _embeddings, _uow,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch), MsOptions.Create(rag ?? new RagOptions()),
            NullLogger<UploadDocumentHandler>.Instance);

    private static byte[] Bytes(string s = "x") => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Unsupported_extension_is_a_validation_error()
    {
        var h = Handler((_, _) => "metin");
        await Assert.ThrowsAsync<ValidationException>(() => h.HandleAsync(new UploadDocumentCommand("virus.exe", Bytes()), default));
    }

    [Fact]
    public async Task Empty_file_is_a_validation_error()
    {
        var h = Handler((_, _) => "metin");
        await Assert.ThrowsAsync<ValidationException>(() => h.HandleAsync(new UploadDocumentCommand("a.txt", []), default));
    }

    [Fact]
    public async Task File_over_the_size_limit_is_rejected()
    {
        var h = Handler((_, _) => "metin", new RagOptions { MaxFileBytes = 10 });
        await Assert.ThrowsAsync<ValidationException>(() => h.HandleAsync(new UploadDocumentCommand("a.txt", new byte[11]), default));
    }

    [Fact]
    public async Task A_corrupt_file_becomes_a_422_not_a_500()
    {
        var h = Handler((_, _) => throw new InvalidDataException("bozuk PDF"));
        await Assert.ThrowsAsync<UnprocessableException>(() => h.HandleAsync(new UploadDocumentCommand("a.pdf", Bytes()), default));
    }

    [Fact]
    public async Task A_scanned_pdf_with_no_text_layer_is_explained()
    {
        var h = Handler((_, _) => "   ");
        var e = await Assert.ThrowsAsync<UnprocessableException>(() => h.HandleAsync(new UploadDocumentCommand("a.pdf", Bytes()), default));
        Assert.Contains("OCR", e.Message);
    }

    [Fact]
    public async Task Chunks_are_embedded_in_batches_and_saved_in_a_single_unit_of_work()
    {
        // 10 kısa paragraf, parça başına ~1 paragraf, 4'lük gruplar halinde gömülür.
        var text = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Paragraf {i}: " + new string('a', 80)));
        var h = Handler((_, _) => text, new RagOptions { ChunkChars = 100, OverlapChars = 0, EmbedBatchSize = 4 });

        var result = await h.HandleAsync(new UploadDocumentCommand("notlar.md", Bytes(text)), default);

        var doc = Assert.Single(_docs.Added);
        Assert.Equal(10, doc.Chunks.Count);
        Assert.Equal(result.ChunkCount, doc.Chunks.Count);
        Assert.Equal([4, 4, 2], _embeddings.Calls.Select(c => c.Count).ToArray());
        Assert.Equal(1, _uow.Saves); // yarım belge kalmasın: tek işlem
        Assert.All(doc.Chunks, c => Assert.Equal(EmbeddingSpec.Dimensions, c.Embedding.Length));
    }

    [Fact]
    public async Task The_document_prefix_is_applied_to_the_text_that_gets_embedded_but_not_to_the_stored_text()
    {
        var h = Handler((_, _) => "Tek paragraf.", new RagOptions { DocumentPrefix = "search_document: " });

        await h.HandleAsync(new UploadDocumentCommand("a.txt", Bytes()), default);

        Assert.StartsWith("search_document: ", _embeddings.Calls.Single().Single());
        Assert.Equal("Tek paragraf.", _docs.Added.Single().Chunks.Single().Text);
    }
}

public class DomainInvariantTests
{
    [Fact]
    public void A_vector_of_the_wrong_size_is_refused_so_search_never_silently_breaks()
    {
        var doc = KnowledgeDocument.Create("a.txt", 10, DateTime.UtcNow);
        var e = Assert.Throws<DomainException>(() => doc.AddChunk("metin", new float[768]));
        Assert.Contains("1024", e.Message);
    }

    [Fact]
    public void Chunk_ordinals_follow_insertion_order()
    {
        var doc = KnowledgeDocument.Create("a.txt", 10, DateTime.UtcNow);
        doc.AddChunk("bir", new float[EmbeddingSpec.Dimensions]);
        doc.AddChunk("iki", new float[EmbeddingSpec.Dimensions]);
        Assert.Equal([0, 1], doc.Chunks.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public void Long_titles_are_cut_with_an_ellipsis_and_newlines_flattened()
    {
        var title = Conversation.MakeTitle(new string('a', 100) + "\nikinci satır");
        Assert.Equal(Conversation.MaxTitleLength + 1, title.Length);
        Assert.EndsWith("…", title);
        Assert.DoesNotContain('\n', title);
    }

    [Fact]
    public void A_conversation_cannot_start_with_a_blank_message()
    {
        Assert.Throws<DomainException>(() => Conversation.Start("  ", DateTime.UtcNow));
    }

    [Fact]
    public void File_names_are_reduced_to_their_last_segment()
    {
        // Yol bileşenleri (../../etc/passwd) veritabanına ve arayüze taşınmaz.
        Assert.Equal("passwd", KnowledgeDocument.Create(@"..\..\etc\passwd", 1, DateTime.UtcNow).FileName);
    }
}
