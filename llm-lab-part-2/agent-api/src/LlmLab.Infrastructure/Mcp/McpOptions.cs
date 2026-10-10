using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace LlmLab.Infrastructure.Mcp;

/// <summary>Ajanın bağlandığı MCP sunucuları ("Mcp" bölümü; ortam değişkeni: Mcp__Servers__0__Url).</summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    [ValidateEnumeratedItems] public List<McpServerEntry> Servers { get; set; } = [];

    /// <summary>Bir sunucuya bağlanma + araç listesi alma için süre sınırı. Aşılırsa sunucu o tur yok sayılır.</summary>
    [Range(1, 120)] public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Bağlanamayan sunucuya yeniden denemeden önce beklenecek süre (her turda bekletmemek için).</summary>
    [Range(1, 3600)] public int RetryAfterSeconds { get; set; } = 15;

    /// <summary>Tek bir araç çağrısının süre sınırı.</summary>
    [Range(1, 600)] public int CallTimeoutSeconds { get; set; } = 60;
}

public sealed class McpServerEntry
{
    /// <summary>Günlük ve telemetride görünen kısa ad.</summary>
    [Required] public string Name { get; set; } = "";

    /// <summary>Streamable HTTP uç noktası, ör. http://localhost:5101/mcp.</summary>
    [Required] public string Url { get; set; } = "";

    /// <summary>
    /// true ise bu sunucunun araçları yalnızca kullanıcı "Belgeler"i açtığında ajana verilir. Belgeler kapalıyken
    /// belge araçları verilmez ve talimat da onlardan bahsetmez.
    /// </summary>
    public bool OnlyWithDocuments { get; set; }
}
