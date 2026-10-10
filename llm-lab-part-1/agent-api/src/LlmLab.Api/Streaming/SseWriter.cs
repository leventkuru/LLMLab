using System.Text.Json;
using System.Text.Json.Serialization;
using LlmLab.Application.Features.Chat;

namespace LlmLab.Api.Streaming;

/// <summary>
/// Sohbet olaylarını Server-Sent Events olarak yazar. Her olay tek satırlık JSON'dur ("data: {...}\n\n");
/// JSON olduğu için metindeki satır sonları akış biçimini bozmaz.
///
/// Olay türleri (arayüzle sözleşme):
///   conversation  {text: "<guid>"}                         sohbet / ajan oturumu kimliği (ilk olay)
///   tool          {name, text: "<argümanlar>"}             ajan bir araç çağırdı
///   sources       {sources: [...]}                         cevapta kullanılan belge parçaları
///   thinking      {text}                                   modelin iç akıl yürütmesi
///   token         {text}                                   asıl cevap
/// </summary>
internal static class SseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(HttpResponse response, ChatStreamEvent evt, CancellationToken ct)
    {
        // Başlıklar ilk olayda yazılır: ondan önce bir hata olursa (ör. sohbet bulunamadı) hata işleyicisi normal bir
        // JSON hata cevabı üretebilir.
        if (!response.HasStarted)
        {
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // ara proxy'ler (nginx) tamponlamasın
        }

        object payload = evt switch
        {
            ConversationStarted e => new { type = "conversation", text = e.ConversationId.ToString() },
            ToolCalled e => new { type = "tool", name = e.Name, text = e.Arguments },
            SourcesAvailable e => new { type = "sources", sources = e.Sources },
            ThinkingChunk e => new { type = "thinking", text = e.Text },
            TokenChunk e => new { type = "token", text = e.Text },
            _ => throw new InvalidOperationException($"Bilinmeyen olay: {evt.GetType().Name}"),
        };

        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
