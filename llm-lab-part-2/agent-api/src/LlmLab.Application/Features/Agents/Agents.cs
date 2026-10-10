using LlmLab.Application.Abstractions;
using LlmLab.Application.Common;
using LlmLab.Domain.Agents;
using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Features.Agents;

// ---- Okuma ve yazma modelleri ----------------------------------------------------------------------------

public sealed record AgentView(
    string Id, string DisplayName, string Description, string Instructions, IReadOnlyList<string> AllowedTools,
    bool CanDelegate, AgentHosting Hosting, bool Enabled, int Version, DateTime UpdatedAt)
{
    public static AgentView From(AgentDefinition d) =>
        new(d.Id, d.DisplayName, d.Description, d.Instructions, d.AllowedTools, d.CanDelegate, d.Hosting, d.Enabled,
            d.Version, d.UpdatedAt);
}

public sealed record SaveAgentCommand(
    string Id, string DisplayName, string? Description, string? Instructions, IReadOnlyList<string>? AllowedTools,
    bool CanDelegate, AgentHosting Hosting, bool Enabled);

// ---- Kullanım senaryoları --------------------------------------------------------------------------------

public sealed class ListAgentsHandler(IAgentRepository agents)
{
    public async Task<IReadOnlyList<AgentView>> HandleAsync(CancellationToken ct) =>
        (await agents.ListAsync(ct)).Select(AgentView.From).ToList();
}

public sealed class GetAgentHandler(IAgentRepository agents)
{
    public async Task<AgentView> HandleAsync(string id, CancellationToken ct) =>
        AgentView.From(await agents.GetAsync(id, ct) ?? throw new NotFoundException("Ajan bulunamadı."));
}

/// <summary>Ajanı oluşturur ya da günceller (PUT semantiği: kimlik URL'den gelir).</summary>
public sealed class SaveAgentHandler(IAgentRepository agents, IUnitOfWork uow, TimeProvider clock)
{
    public async Task<AgentView> HandleAsync(SaveAgentCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await agents.GetAsync(cmd.Id, ct);

        // Varsayılan ajan her zaman etkin kalır: sohbetin geri dönüş noktasıdır.
        var enabled = cmd.Enabled || cmd.Id == Conversation.DefaultAgentId;

        if (existing is null)
        {
            existing = AgentDefinition.Create(
                cmd.Id, cmd.DisplayName, cmd.Description ?? "", cmd.Instructions ?? "", cmd.AllowedTools,
                cmd.CanDelegate, cmd.Hosting, enabled, now);
            agents.Add(existing);
        }
        else
        {
            existing.Update(
                cmd.DisplayName, cmd.Description ?? "", cmd.Instructions ?? "", cmd.AllowedTools,
                cmd.CanDelegate, cmd.Hosting, enabled, now);
        }

        await uow.SaveChangesAsync(ct);
        return AgentView.From(existing);
    }
}

/// <summary>"Şunlar çalışsın" anahtarı: ajanı etkinleştirir / devre dışı bırakır.</summary>
public sealed class SetAgentEnabledHandler(IAgentRepository agents, IUnitOfWork uow, TimeProvider clock)
{
    public async Task<AgentView> HandleAsync(string id, bool enabled, CancellationToken ct)
    {
        var agent = await agents.GetAsync(id, ct) ?? throw new NotFoundException("Ajan bulunamadı.");
        if (!enabled && id == Conversation.DefaultAgentId)
            throw new ValidationException("Varsayılan ajan devre dışı bırakılamaz.");

        agent.SetEnabled(enabled, clock.GetUtcNow().UtcDateTime);
        await uow.SaveChangesAsync(ct);
        return AgentView.From(agent);
    }
}

public sealed class DeleteAgentHandler(IAgentRepository agents)
{
    public async Task HandleAsync(string id, CancellationToken ct)
    {
        if (id == Conversation.DefaultAgentId)
            throw new ValidationException("Varsayılan ajan silinemez.");
        if (!await agents.DeleteAsync(id, ct)) throw new NotFoundException("Ajan bulunamadı.");
    }
}

public sealed class ListToolsHandler(IToolInventory tools)
{
    public Task<IReadOnlyList<ToolInfo>> HandleAsync(CancellationToken ct) => tools.ListAsync(ct);
}

/// <summary>
/// Başlangıç (tohum) ajanları: kayıt defteri boşken ilk açılışta eklenir. Çalışan ortamdaki düzenlemeler korunur;
/// yalnızca eksik kimlikler eklenir.
/// </summary>
public static class DefaultAgents
{
    public static IReadOnlyList<AgentDefinition> Create(DateTime now) =>
    [
        AgentDefinition.Create("assistant", "Asistan", "Genel amaçlı asistan: verilen tüm araçları kullanabilir.",
            "", [], canDelegate: false, AgentHosting.Local, enabled: true, now),

        AgentDefinition.Create("researcher", "Araştırmacı", "Yüklü belgelerde arama yapıp kaynaklı özet çıkarır.",
            "Rolün: belge araştırmacısı. Soruya cevap vermeden önce belgelerde ara; bulduklarını kaynak numaralarıyla, " +
            "kısa maddeler halinde özetle. Belgelerde olmayan bir şeyi uydurma.",
            ["search_documents", "list_documents"], canDelegate: false, AgentHosting.Local, enabled: true, now),

        AgentDefinition.Create("analyst", "Analist", "Sayısal hesapları ve kısa veri analizlerini araçlarla doğrulayarak yapar.",
            "Rolün: analist. Sayısal bir sonucu asla kafandan verme: basit işlemler için calculate, çok adımlı ya da " +
            "veri işleyen hesaplar için run_python kullan. Cevabında kullandığın yöntemi tek cümleyle belirt.",
            ["calculate", "run_python", "get_current_time"], canDelegate: false, AgentHosting.Local, enabled: true, now),

        AgentDefinition.Create("coder", "Kodcu", "Küçük Python programları yazar, izole ortamda çalıştırır ve sonucu doğrular.",
            "Rolün: yazılımcı. İstenen işi yapan kısa bir Python programı yaz, run_python ile çalıştır, çıktıyı kontrol et; " +
            "hata varsa düzeltip yeniden dene (en fazla 3 deneme). Ortamda ağ ve üçüncü taraf paket yoktur, yalnızca " +
            "standart kütüphane. Sonuçta çalışan kodu ve çıktısını göster.",
            ["run_python", "calculate"], canDelegate: false, AgentHosting.Local, enabled: true, now),

        AgentDefinition.Create("coordinator", "Koordinatör", "Karmaşık işi alt ajanlara böler, sonuçları birleştirir.",
            "Rolün: koordinatör. İsteği küçük görevlere böl ve her birini uygun uzman ajana devret; bağımsız görevleri " +
            "önce hepsini başlat, sonra sonuçları al ve tek, tutarlı bir cevapta birleştir. Gerekirse stop_subagent " +
            "ile görevi durdur. Selamlaşma ve genel sohbeti kendin yürüt.",
            ["get_current_time"], canDelegate: true, AgentHosting.Local, enabled: true, now),
    ];
}
