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

    /// <summary>
    /// "Belgeler" açıkken ilk belge aramasını sistem yapar ve sonucu ajanın konuşmasına araç çağrısı olarak koyar;
    /// model gerekirse yine kendisi arayabilir. Neden: ölçüldü, düşünme açıkken qwen3:8b aracı sık atlıyor ve
    /// kaynaksız, uydurma cevap veriyor. false = saf "agentic" davranış (aracı çağırıp çağırmamak modelin kararı).
    /// </summary>
    public bool SearchFirst { get; set; } = true;

    /// <summary>Modelin bağlam penceresi (token). Ollama'nın varsayılanı küçüktür; oturum geçmişi sığsın diye büyütüldü.</summary>
    [Range(1024, 131072)] public int ContextTokens { get; set; } = 8192;
}
