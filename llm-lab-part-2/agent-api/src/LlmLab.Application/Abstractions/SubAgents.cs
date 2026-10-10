namespace LlmLab.Application.Abstractions;

public enum SubAgentStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
}

/// <summary>Bir alt ajan görevinin anlık durumu.</summary>
/// <param name="TaskId">Görev kimliği (koordinatör bununla sonuç ister / durdurur).</param>
/// <param name="Result">Tamamlandıysa alt ajanın cevabı (kısaltılmış olabilir).</param>
/// <param name="ToolCalls">Alt ajanın şimdiye dek çağırdığı araçların adları (ilerleme göstergesi).</param>
public sealed record SubAgentSnapshot(
    string TaskId, string AgentId, string Task, SubAgentStatus Status, string? Result, string? Error,
    DateTime StartedAt, DateTime? FinishedAt, IReadOnlyList<string> ToolCalls);

/// <summary>
/// Alt ajan görevleri portu: başlat / durum-sonuç al / durdur. Faz 2'de uygulaması süreç içidir (bellekte, arka plan
/// görevi); API yeniden başlarsa görevler kaybolur. Faz 3'te aynı port Dapr (iş akışı + aktör) ile uygulanacak;
/// koordinatörün kodu değişmez. Bu yüzden port burada, uygulama altyapıdadır.
/// </summary>
public interface ISubAgentRunner
{
    /// <summary>Görevi başlatır ve hemen döner. Ajan yok / devre dışı / devredemez türdeyse hata verir.</summary>
    Task<SubAgentSnapshot> StartAsync(string agentId, string task, CancellationToken ct);

    SubAgentSnapshot? Get(string taskId);

    /// <summary>Görev bitene ya da süre dolana dek bekler; ardından güncel durumu döndürür (süre dolarsa hâlâ Running).</summary>
    Task<SubAgentSnapshot?> WaitAsync(string taskId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Çalışan görevi iptal eder. Görev yoksa ya da bitmişse false.</summary>
    bool Stop(string taskId);

    IReadOnlyList<SubAgentSnapshot> List();
}

/// <summary>Kayıt defteri düzenleyicisinin gösterdiği, keşfedilen araç bilgisi.</summary>
public sealed record ToolInfo(string Name, string Description, string Server, bool DocumentTool);

/// <summary>Ajanlara verilebilecek araçların envanteri (MCP sunucularından keşfedilen).</summary>
public interface IToolInventory
{
    Task<IReadOnlyList<ToolInfo>> ListAsync(CancellationToken ct);
}
