using System.Text.RegularExpressions;
using LlmLab.Domain.Common;

namespace LlmLab.Domain.Agents;

/// <summary>Ajanın nerede çalıştığı: bu API'nin içinde ya da A2A ile ayrı bir serviste.</summary>
public enum AgentHosting
{
    Local,
    Remote,
}

/// <summary>
/// Ajan kayıt defterindeki bir kayıt: ajan KOD değil VERİDİR. Bir ajanı tanımlayan her şey (rol talimatı, hangi araçları
/// kullanabileceği, devredebilir mi, nerede çalışır) burada durur; çalışma zamanı bu kayıttan bir <c>AIAgent</c> kurar.
/// Neden veri? Ajan eklemek/ayarlamak dağıtım gerektirmez, arayüzden seçilebilir, sürümlenir ve denetlenebilir olur.
/// </summary>
public sealed partial class AgentDefinition
{
    public const int MaxInstructionsLength = 4000;
    public const int MaxToolCount = 32;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex ToolNamePattern();

    private AgentDefinition() { } // EF Core

    /// <summary>Kısa, URL'de kullanılabilir kimlik (ör. "analyst"). Değişmez.</summary>
    public string Id { get; private set; } = "";

    public string DisplayName { get; private set; } = "";
    public string Description { get; private set; } = "";

    /// <summary>
    /// Ajanın rolünü anlatan metin. Sistem talimatına EKLENİR (araç ve kaynak kuralları sistemden gelir); böylece bir
    /// ajanı düzenlemek, "kaynak numarası kullan" gibi güvenlik kurallarını devre dışı bırakamaz.
    /// </summary>
    public string Instructions { get; private set; } = "";

    /// <summary>Ajanın kullanabileceği araç adları (MCP'den). Boş liste = o turda verilen tüm araçlar.</summary>
    public IReadOnlyList<string> AllowedTools { get; private set; } = [];

    /// <summary>Doğruysa ajan, diğer ajanlara iş devredebilir (koordinatör): alt ajan başlat / sonuç al / durdur araçları.</summary>
    public bool CanDelegate { get; private set; }

    public AgentHosting Hosting { get; private set; }

    /// <summary>Devre dışı ajan sohbette seçilemez ve alt ajan olarak çalıştırılamaz ("şunlar çalışsın" anahtarı).</summary>
    public bool Enabled { get; private set; } = true;

    public int Version { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public static AgentDefinition Create(
        string id, string displayName, string description, string instructions, IEnumerable<string>? allowedTools,
        bool canDelegate, AgentHosting hosting, bool enabled, DateTime now)
    {
        if (!IdPattern().IsMatch(id ?? ""))
            throw new DomainException("Ajan kimliği küçük harfle başlamalı; yalnızca küçük harf, rakam ve '-' içerebilir (2-32 karakter).");

        var agent = new AgentDefinition { Id = id!, Version = 0 };
        agent.Apply(displayName, description, instructions, allowedTools, canDelegate, hosting, enabled, now);
        return agent;
    }

    public void Update(
        string displayName, string description, string instructions, IEnumerable<string>? allowedTools,
        bool canDelegate, AgentHosting hosting, bool enabled, DateTime now) =>
        Apply(displayName, description, instructions, allowedTools, canDelegate, hosting, enabled, now);

    public void SetEnabled(bool enabled, DateTime now)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Version++;
        UpdatedAt = now;
    }

    private void Apply(
        string displayName, string description, string instructions, IEnumerable<string>? allowedTools,
        bool canDelegate, AgentHosting hosting, bool enabled, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("Ajan adı boş olamaz.");
        if (displayName.Length > 60)
            throw new DomainException("Ajan adı en fazla 60 karakter olabilir.");
        if ((description ?? "").Length > 300)
            throw new DomainException("Açıklama en fazla 300 karakter olabilir.");
        if ((instructions ?? "").Length > MaxInstructionsLength)
            throw new DomainException($"Talimat en fazla {MaxInstructionsLength} karakter olabilir.");

        var tools = (allowedTools ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (tools.Count > MaxToolCount)
            throw new DomainException($"En fazla {MaxToolCount} araç seçilebilir.");
        if (tools.FirstOrDefault(t => !ToolNamePattern().IsMatch(t)) is { } bad)
            throw new DomainException($"Geçersiz araç adı: '{bad}'.");

        DisplayName = displayName.Trim();
        Description = (description ?? "").Trim();
        Instructions = (instructions ?? "").Trim();
        AllowedTools = tools;
        CanDelegate = canDelegate;
        Hosting = hosting;
        Enabled = enabled;
        Version++;
        UpdatedAt = now;
    }
}
