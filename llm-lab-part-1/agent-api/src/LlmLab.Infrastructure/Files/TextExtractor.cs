using System.Text;
using LlmLab.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LlmLab.Infrastructure.Files;

/// <summary>.txt, .md ve .pdf dosyalarından düz metin çıkarır.</summary>
internal sealed class TextExtractor : ITextExtractor
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".md", ".pdf"];

    public string Extract(string fileName, byte[] content)
    {
        try
        {
            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".txt" or ".md" => FromText(content),
                ".pdf" => FromPdf(content),
                var ext => throw new NotSupportedException($"Desteklenmeyen dosya türü: {ext}"),
            };
        }
        catch (Exception e) when (e is not NotSupportedException and not InvalidDataException)
        {
            // Kütüphane özel hataları (bozuk PDF vb.) tek bir türe indirgenir; çağıran kütüphaneyi bilmek zorunda kalmaz.
            throw new InvalidDataException("Dosya okunamadı.", e);
        }
    }

    // BOM varsa ondan, yoksa UTF-8 olarak okur.
    private static string FromText(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Yalnızca metin katmanı olan PDF'lerden metin çıkar; taranmış (resim) PDF için OCR gerekir.
    private static string FromPdf(byte[] content)
    {
        using var pdf = PdfDocument.Open(content);
        return string.Join("\n\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
    }
}
