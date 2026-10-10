using System.Text;
using System.Text.RegularExpressions;

namespace LlmLab.Application.Rag;

/// <summary>
/// Uzun metni embedding için uygun boyutta parçalara böler (saf fonksiyon, dış bağımlılığı yok).
///
/// Neden parçalıyoruz? Bir embedding tüm metnin "ortalama anlamını" tutar. 50 sayfalık bir belgeden tek vektör
/// çıkarırsak her soruya biraz benzer, hiçbirine iyi benzemez. Küçük parçalar belirli bir konuyu temsil eder.
///
/// Strateji: paragraflara böl (anlam sınırları), markdown başlığını altındaki metne yapıştır, paragrafları
/// ~maxChars'a kadar birleştir, çok uzun paragrafı cümlelere, çok uzun cümleyi kelime sınırına göre böl.
/// Her yeni parça, bir öncekinin son ~overlapChars karakteriyle başlar: iki parçanın sınırındaki bilgi kaybolmaz.
/// Bu yüzden bir parça en fazla maxChars + overlapChars kadar olabilir.
/// </summary>
public static partial class TextChunker
{
    public static List<string> Split(string text, int maxChars = 600, int overlapChars = 100)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 1) Anlamlı birimlere ayır (her biri en fazla maxChars).
        var units = new List<string>();
        string? heading = null; // altındaki metinle birleştirilmeyi bekleyen markdown başlığı
        foreach (var paragraph in ParagraphBreak().Split(text))
        {
            // PDF'lerde satır sonları rastgele olabildiği için boşlukları tek boşluğa indir.
            var p = Whitespace().Replace(paragraph, " ").Trim();
            if (p.Length == 0) continue;

            // Başlık tek başına kalırsa bir parçanın SONUNA düşebilir; içeriği başlıksız bir sonraki parçada kalır ve
            // gömme o içeriği konusuyla ilişkilendiremez. Bu yüzden başlığı sonraki metne yapıştır.
            if (heading is not null)
            {
                p = heading + "\n" + p;
                heading = null;
            }
            if (IsHeadingOnly(p))
            {
                heading = p;
                continue;
            }

            if (p.Length <= maxChars) units.Add(p);
            else units.AddRange(SplitLong(p, maxChars));
        }
        if (heading is not null) units.Add(heading); // belge başlıkla bitiyorsa

        // 2) Birimleri maxChars'a kadar birleştir; yeni parçayı öncekinin sonuyla başlat (örtüşme).
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var unit in units)
        {
            if (current.Length > 0 && current.Length + 1 + unit.Length > maxChars)
            {
                var done = current.ToString();
                chunks.Add(done);
                current.Clear().Append(Tail(done, overlapChars));
            }
            if (current.Length > 0) current.Append('\n');
            current.Append(unit);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static bool IsHeadingOnly(string p) =>
        p.Split('\n').All(line => line.StartsWith('#') && line.Length <= 100);

    private static IEnumerable<string> SplitLong(string paragraph, int maxChars)
    {
        var current = new StringBuilder();
        foreach (var sentence in SentenceBreak().Split(paragraph))
        {
            foreach (var piece in sentence.Length <= maxChars ? [sentence] : HardSplit(sentence, maxChars))
            {
                if (current.Length > 0 && current.Length + 1 + piece.Length > maxChars)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(piece);
            }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static IEnumerable<string> HardSplit(string s, int maxChars)
    {
        while (s.Length > maxChars)
        {
            var cut = s.LastIndexOf(' ', maxChars);
            if (cut <= 0) cut = maxChars; // hiç boşluk yok (ör. çok uzun bir URL)
            yield return s[..cut].Trim();
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }

    /// <summary>Metnin son ~n karakteri, kelimenin ortasından başlamayacak şekilde.</summary>
    private static string Tail(string s, int n)
    {
        if (n <= 0 || s.Length <= n) return n <= 0 ? "" : s;
        var start = s.Length - n;
        var space = s.IndexOf(' ', start);
        return space < 0 ? s[start..] : s[(space + 1)..];
    }

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex ParagraphBreak();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"(?<=[.!?…])\s+")] private static partial Regex SentenceBreak();
}
