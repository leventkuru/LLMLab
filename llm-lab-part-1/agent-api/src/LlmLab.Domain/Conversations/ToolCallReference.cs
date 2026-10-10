namespace LlmLab.Domain.Conversations;

/// <summary>
/// Ajanın bir cevap sırasında çağırdığı aracın kaydı (değer nesnesi). Transkriptte saklanır; kullanıcı "ajan ne yaptı?"
/// sorusunu sohbeti sonradan açınca da görebilsin. Boş liste = araç çağrılmadı.
/// </summary>
/// <param name="Name">Araç adı (ör. search_documents).</param>
/// <param name="Arguments">Okunur biçimde argümanlar (ör. "query=aurora politikaları").</param>
/// <param name="Position">
/// Aracın, cevap metninin kaçıncı karakterinde çağrıldığı. Arayüz metni bu noktadan bölüp araç satırını metinlerin
/// ARASINA koyar: "ön cümle → [araç kullandı] → asıl cevap".
/// </param>
public sealed record ToolCallReference(string Name, string? Arguments, int Position = 0);
