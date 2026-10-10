using LlmLab.Application.Rag;

namespace LlmLab.Application.Tests.Rag;

public class TextChunkerTests
{
    [Fact]
    public void Empty_text_yields_no_chunks()
    {
        Assert.Empty(TextChunker.Split("   \n\n  "));
    }

    [Fact]
    public void Short_text_is_a_single_chunk()
    {
        var chunks = TextChunker.Split("Kısa bir paragraf.");
        Assert.Equal(["Kısa bir paragraf."], chunks);
    }

    [Fact]
    public void Heading_is_never_orphaned_at_the_end_of_a_chunk()
    {
        // Ders: başlık bir parçanın SONUNA düşerse içeriği başlıksız kalır ve gömme konusunu bilemez.
        var para = string.Join(' ', Enumerable.Repeat("kelime", 70)); // ~420 karakter
        var text = $"## Birinci bölüm\n\n{para}\n\n## İkinci bölüm\n\n{para}";

        var chunks = TextChunker.Split(text, maxChars: 500, overlapChars: 0);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.False(
            c.TrimEnd().Split('\n').Last().StartsWith('#'),
            $"Başlıkla biten parça: …{(c.Length > 30 ? c[^30..] : c)}"));
        Assert.Contains(chunks, c => c.StartsWith("## İkinci bölüm"));
    }

    [Fact]
    public void Heading_is_glued_to_the_text_below_it()
    {
        var chunks = TextChunker.Split("## Uzaktan çalışma\n\nHaftada üç gün evden çalışılabilir.");
        Assert.Single(chunks);
        Assert.Equal("## Uzaktan çalışma\nHaftada üç gün evden çalışılabilir.", chunks[0]);
    }

    [Fact]
    public void Consecutive_chunks_overlap_so_boundary_information_is_not_lost()
    {
        var sentences = Enumerable.Range(1, 40).Select(i => $"Bu {i}. cümledir ve oldukça uzundur.");
        var text = string.Join(' ', sentences);

        var chunks = TextChunker.Split(text, maxChars: 300, overlapChars: 80);

        Assert.True(chunks.Count > 2);
        for (var i = 1; i < chunks.Count; i++)
        {
            // Bir sonraki parçanın başı, öncekinin sonundan gelen metni içerir.
            var firstWords = string.Join(' ', chunks[i].Split(' ').Take(3));
            Assert.Contains(firstWords, chunks[i - 1]);
        }
    }

    [Fact]
    public void Chunks_stay_within_max_plus_overlap()
    {
        var text = string.Join(". ", Enumerable.Range(1, 200).Select(i => $"Cümle numarası {i} burada"));
        var chunks = TextChunker.Split(text, maxChars: 400, overlapChars: 100);
        Assert.All(chunks, c => Assert.True(c.Length <= 400 + 100 + 1, $"Çok uzun parça: {c.Length}"));
    }

    [Fact]
    public void A_single_unbroken_word_longer_than_max_is_hard_split()
    {
        var chunks = TextChunker.Split(new string('x', 2000), maxChars: 500, overlapChars: 0);
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(c.Length <= 501));
    }

    [Fact]
    public void Pdf_style_hard_wrapped_lines_are_joined_into_one_paragraph()
    {
        var chunks = TextChunker.Split("Bu cümle PDF'te\nsatır sonlarıyla\nbölünmüş.");
        Assert.Equal(["Bu cümle PDF'te satır sonlarıyla bölünmüş."], chunks);
    }
}
