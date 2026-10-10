using System.Globalization;
using System.Text.RegularExpressions;

namespace LlmLab.Application.Rag;

/// <summary>
/// Arama sorgusundan ayırt edici anahtar kelimeleri çıkarır (hibrit aramanın kelime ayağı için).
/// Türkçe ekli bir dil olduğu için kelimeler ilk 5 harfe kısaltılır (kaba kök): "çalışabilirim" -> "çalış",
/// böylece belgedeki "çalışabilir" ile eşleşir.
/// </summary>
public static partial class QueryTokenizer
{
    public const int MaxTokens = 5;
    private const int StemLength = 5;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // Sorgudan atılan, ayırt edici olmayan sözcükler (soru kalıpları, edatlar, dolgu).
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "nedir", "neden", "nasıl", "nerede", "nereye", "hangi", "kaç", "kim", "kime", "kimin", "için", "ile", "veya",
        "ama", "fakat", "hakkında", "bilgi", "verir", "ver", "söyle", "anlat", "anlatır", "midir", "mıdır", "olan",
        "olarak", "gibi", "daha", "çok", "var", "yok", "bana", "bize", "lütfen", "ise", "her", "tüm", "bütün",
        "kadar", "sonra", "önce", "belge", "belgede", "belgelerde", "nelerdir", "neler", "bir",
    };

    public static IReadOnlyList<string> Tokenize(string query) =>
        Word().Matches(query.ToLower(Turkish))
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Select(w => w.Length > StemLength ? w[..StemLength] : w)
            .Distinct()
            .Take(MaxTokens)
            .ToList();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
