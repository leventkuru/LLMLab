using System.ComponentModel.DataAnnotations;

namespace LlmLab.Application.Options;

/// <summary>Ajan ayarları ("Agent" bölümü).</summary>
public sealed class AgentSettings
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Ajan oturumunda tutulan en fazla mesaj sayısı (araç çağrıları ve sonuçları da sayılır). Daha eskisi
    /// oturumdan atılır; modelin bağlam penceresi sonsuz değildir.
    /// </summary>
    [Range(2, 200)] public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>Modelin bağlam penceresi (token). Ollama'nın varsayılanı küçüktür; oturum geçmişi sığsın diye büyütüldü.</summary>
    [Range(1024, 131072)] public int ContextTokens { get; set; } = 8192;
}
