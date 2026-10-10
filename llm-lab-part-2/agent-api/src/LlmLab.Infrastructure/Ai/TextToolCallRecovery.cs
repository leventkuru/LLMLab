using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LlmLab.Infrastructure.Ai;

/// <summary>
/// Küçük modellerin (qwen3:8b ile gözlendi) araç çağrısını yapılandırılmış çağrı yerine DÜZ METİN olarak yazması:
/// <c>run_python {"code": "..."}</c> ya da <c>{"name": "run_python", "arguments": {...}}</c>. Sağlayıcı bunu araç çağrısı
/// olarak ayrıştıramaz; araç hiç çalışmaz, metin "cevap" olarak akar ve (daha kötüsü) sonraki ajan o metinden sonuç
/// uydurur. Bu ayrıştırıcı, metnin TAMAMI bilinen bir aracın çağrısıysa onu gerçek çağrıya çevirir.
/// Muhafazakârdır: ad bilinen bir araca eşleşmeli ve JSON geçerli olmalı; aksi halde metne dokunmaz.
/// </summary>
internal static partial class TextToolCallParser
{
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*[:(]?\s*(?<json>\{.*\})\s*\)?\s*$", RegexOptions.Singleline)]
    private static partial Regex NamedCall();

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true };

    public static bool TryParse(string text, IReadOnlyCollection<string> toolNames, out FunctionCallContent? call)
    {
        call = null;
        var s = StripFence(text.Trim());
        if (s.Length == 0 || toolNames.Count == 0) return false;

        try
        {
            // 1) {"name": "arac", "arguments": {...}}  (OpenAI/Qwen tarzı)
            if (s[0] == '{')
            {
                using var doc = JsonDocument.Parse(s, Lenient);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return false;

                var name = nameEl.GetString()!;
                if (!toolNames.Contains(name)) return false;

                var args = root.TryGetProperty("arguments", out var a) ? a
                    : root.TryGetProperty("parameters", out var p) ? p
                    : default;
                call = Build(name, args);
                return call is not null;
            }

            // 2) arac {json}  /  arac({json})  /  arac: {json}
            var m = NamedCall().Match(s);
            if (!m.Success || !toolNames.Contains(m.Groups["name"].Value)) return false;

            using var argsDoc = JsonDocument.Parse(m.Groups["json"].Value, Lenient);
            call = Build(m.Groups["name"].Value, argsDoc.RootElement);
            return call is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Metin tamamen bir araç çağrısına benzeyebilir mi? Akışı ne zaman tamponlayacağımızı belirler.</summary>
    public static bool CouldBeCall(string trimmedStart, IReadOnlyCollection<string> toolNames)
    {
        if (trimmedStart.Length == 0) return true;
        if (trimmedStart[0] == '{' || trimmedStart.StartsWith("```", StringComparison.Ordinal)) return true;

        foreach (var name in toolNames)
        {
            // Ad (tamamı) yazıldıysa ve ardından ad karakteri değil bir ayırıcı geliyorsa ya da ad henüz yazılıyorsa.
            if (trimmedStart.StartsWith(name, StringComparison.Ordinal))
            {
                if (trimmedStart.Length == name.Length) return true;
                var next = trimmedStart[name.Length];
                if (!(char.IsLetterOrDigit(next) || next is '_' or '-' or '.')) return true;
            }
            else if (name.StartsWith(trimmedStart, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static FunctionCallContent? Build(string name, JsonElement args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in args.EnumerateObject()) dict[p.Name] = p.Value.Clone();
        }
        else if (args.ValueKind != JsonValueKind.Undefined && args.ValueKind != JsonValueKind.Null)
        {
            return null; // argümanlar nesne değil: güvenilir değil
        }
        return new FunctionCallContent("recovered-" + Guid.NewGuid().ToString("N")[..12], name, dict);
    }

    private static string StripFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        var firstLineEnd = s.IndexOf('\n');
        if (firstLineEnd < 0) return s;
        var body = s[(firstLineEnd + 1)..];
        var close = body.LastIndexOf("```", StringComparison.Ordinal);
        return (close >= 0 ? body[..close] : body).Trim();
    }
}

/// <summary>
/// Modelin düz metin olarak yazdığı araç çağrısını gerçek araç çağrısına çeviren IChatClient sarmalayıcısı. Araç yoksa
/// ya da metin bir araç çağrısı gibi başlamıyorsa akışa hiç dokunmaz (gecikme eklemez); yalnızca çağrı gibi BAŞLAYAN
/// metni sonuna dek tutar ve ayrıştırılamıyorsa olduğu gibi geri verir.
/// </summary>
internal sealed class TextToolCallRecoveryClient(IChatClient inner, ILogger<TextToolCallRecoveryClient>? log = null)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var names = ToolNames(options);
        if (names.Count == 0) return response;

        foreach (var message in response.Messages)
        {
            if (message.Role != ChatRole.Assistant || message.Contents.Any(c => c is FunctionCallContent)) continue;
            if (TextToolCallParser.TryParse(message.Text, names, out var call) && call is not null)
            {
                log?.LogWarning("Metin olarak yazılmış araç çağrısı gerçek çağrıya çevrildi: {Tool}", call.Name);
                message.Contents = [call];
            }
        }
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var names = ToolNames(options);
        if (names.Count == 0)
        {
            await foreach (var u in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return u;
            yield break;
        }

        var held = new List<ChatResponseUpdate>();
        var text = new StringBuilder();
        var passThrough = false;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // Metin taşımayan güncellemeler (düşünme, gerçek araç çağrısı, kullanım bilgisi) hemen geçer.
            if (passThrough || string.IsNullOrEmpty(update.Text))
            {
                yield return update;
                continue;
            }

            held.Add(update);
            text.Append(update.Text);

            if (!TextToolCallParser.CouldBeCall(text.ToString().TrimStart(), names))
            {
                passThrough = true;
                foreach (var h in held) yield return h;
                held.Clear();
            }
        }

        if (held.Count == 0) yield break;

        if (TextToolCallParser.TryParse(text.ToString(), names, out var call) && call is not null)
        {
            log?.LogWarning("Metin olarak yazılmış araç çağrısı gerçek çağrıya çevrildi: {Tool}", call.Name);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
        }
        else
        {
            foreach (var h in held) yield return h; // çağrı değildi: metni olduğu gibi ver
        }
    }

    private static IReadOnlyCollection<string> ToolNames(ChatOptions? options) =>
        options?.Tools?.OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];
}
