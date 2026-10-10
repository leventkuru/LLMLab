using LlmLab.Application.Features.Agents;
using Microsoft.EntityFrameworkCore;

namespace LlmLab.Infrastructure.Persistence;

/// <summary>
/// Başlangıç ajanlarını ekler (yalnızca eksik kimlikleri). Kullanıcının kayıt defterinde yaptığı düzenlemeler ve
/// sildikleri/devre dışı bıraktıkları korunur: mevcut kayıtlara dokunulmaz. Silinmiş bir başlangıç ajanı bir
/// sonraki açılışta yeniden eklenir; bu bilinçli bir sadelik (silmek yerine devre dışı bırakın).
/// </summary>
internal static class AgentSeeder
{
    public static async Task SeedAsync(AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var existing = await db.Agents.Select(a => a.Id).ToListAsync(ct);
        var missing = DefaultAgents.Create(clock.GetUtcNow().UtcDateTime).Where(d => !existing.Contains(d.Id)).ToList();
        if (missing.Count == 0) return;

        db.Agents.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
