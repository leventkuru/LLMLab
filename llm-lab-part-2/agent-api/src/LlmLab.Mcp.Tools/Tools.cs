using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace LlmLab.Mcp.Tools;

/// <summary>Araç sunucusu ayarları ("Tools" bölümü).</summary>
public sealed class ToolsOptions
{
    public const string SectionName = "Tools";

    /// <summary>
    /// `get_current_time` için varsayılan saat dilimi (IANA adı). Konteynerlerde sunucu saati UTC'dir; "saat kaç?"
    /// cevabı kullanıcının saatiyle uyuşsun diye ayrı ayar. Bulunamazsa sunucunun yerel dilimi kullanılır.
    /// </summary>
    public string DefaultTimeZone { get; set; } = "Europe/Istanbul";
}

/// <summary>
/// Saat aracı. Açıklama (Description) modelin aracı seçerken okuduğu tek şeydir: ne zaman kullanılacağını ve
/// parametreleri net söylemelidir.
/// </summary>
[McpServerToolType]
public static class ClockTools
{
    private static readonly string[] WeekDays =
        ["Pazar", "Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi"];

    [McpServerTool(Name = "get_current_time", ReadOnly = true)]
    [Description(
        "Şu anki tarihi, saati ve haftanın gününü döndürür. Tarih ya da saat sorulduğunda kullan; bunları tahmin etme. " +
        "timeZone: isteğe bağlı IANA saat dilimi adı (ör. Europe/Istanbul, America/New_York); boşsa varsayılan dilim kullanılır.")]
    public static string GetCurrentTime(
        TimeProvider clock,
        IOptions<ToolsOptions> options,
        [Description("İsteğe bağlı IANA saat dilimi adı, ör. Europe/Istanbul")] string? timeZone = null)
    {
        var requested = !string.IsNullOrWhiteSpace(timeZone);
        var zone = ResolveZone(requested ? timeZone!.Trim() : options.Value.DefaultTimeZone, out var found);
        if (requested && !found)
        {
            return $"Bilinmeyen saat dilimi: '{timeZone}'. IANA adı kullan (ör. Europe/Istanbul, America/New_York).";
        }

        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        return string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm}, {WeekDays[(int)now.DayOfWeek]}, UTC{now:zzz} (saat dilimi: {zone.Id})");
    }

    /// <summary>Saat dilimi adını çözer; bulunamazsa sunucunun yerel dilimine düşer (found=false).</summary>
    private static TimeZoneInfo ResolveZone(string id, out bool found)
    {
        try
        {
            found = true;
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            found = false;
            return TimeZoneInfo.Local;
        }
    }
}

/// <summary>Hesap aracı. Modeller aritmetikte hata yapar; hesap modele değil buraya yaptırılır.</summary>
[McpServerToolType]
public static class CalculatorTools
{
    [McpServerTool(Name = "calculate", ReadOnly = true)]
    [Description(
        "Aritmetik ifadeyi hesaplar ve sonucu döndürür. Sayısal her hesapta kullan; hesabı kafandan yapma. " +
        "expression: yalnızca sayılar, + - * / % ^ işleci, parantez ve sqrt(x), abs(x) içeren ifade (ör. (12.5+3)*4).")]
    public static string Calculate([Description("Hesaplanacak ifade, ör. (12.5+3)*4")] string expression) =>
        ExpressionCalculator.Describe(expression);
}
