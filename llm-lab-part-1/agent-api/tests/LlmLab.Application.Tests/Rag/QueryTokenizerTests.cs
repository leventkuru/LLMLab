using LlmLab.Application.Rag;

namespace LlmLab.Application.Tests.Rag;

public class QueryTokenizerTests
{
    [Fact]
    public void Question_words_and_stopwords_are_dropped()
    {
        // "nedir", "hakkında", "bilgi" ayırt edici değildir; "aurora" kalır (kök: auror).
        Assert.Equal(["auror"], QueryTokenizer.Tokenize("aurora nedir"));
        Assert.Equal(["auror", "kahve"], QueryTokenizer.Tokenize("Aurora Kahve hakkında bilgi ver"));
    }

    [Fact]
    public void Long_words_are_cut_to_a_rough_stem_so_inflected_forms_match()
    {
        // "çalışabilirim" (sorgu) belgedeki "çalışabilir" ile aynı kökü paylaşmalı.
        Assert.Equal(QueryTokenizer.Tokenize("çalışabilirim"), QueryTokenizer.Tokenize("çalışabilir"));
        Assert.Equal(["çalış"], QueryTokenizer.Tokenize("çalışabilirim"));
    }

    [Fact]
    public void Turkish_casing_is_handled()
    {
        // 'İ' -> 'i', 'I' -> 'ı' (tr-TR); ASCII lower olsaydı "i̇zin" gibi bozuk bir şey çıkardı.
        Assert.Equal(["izin"], QueryTokenizer.Tokenize("İZİN"));
    }

    [Fact]
    public void Tokens_are_distinct_and_capped()
    {
        var tokens = QueryTokenizer.Tokenize("elma elma armut kiraz muz portakal çilek üzüm");
        Assert.Equal(QueryTokenizer.MaxTokens, tokens.Count);
        Assert.Equal(tokens.Distinct().Count(), tokens.Count);
    }

    [Fact]
    public void Very_short_tokens_are_ignored()
    {
        Assert.Empty(QueryTokenizer.Tokenize("ne o bu ve"));
    }

    [Fact]
    public void Punctuation_never_leaks_into_tokens()
    {
        // Token'lar LIKE kalıbına gider; yalnızca harf/rakam olmalı.
        Assert.All(QueryTokenizer.Tokenize("%izin_ [kayıt]; 'drop'--"), t => Assert.Matches(@"^[\p{L}\p{N}]+$", t));
    }
}
