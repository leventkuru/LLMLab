using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LlmLab.Application.Telemetry;

/// <summary>
/// Uygulamanın özel iz (trace) ve metrik tanımları.
///
/// Neden Application katmanında ve neden OpenTelemetry paketi yok? ActivitySource ve Meter .NET'in kendi (BCL)
/// türleridir: "bir şey ölçüldü" demek için dış paket gerekmez. OpenTelemetry SDK'sı (verileri toplayıp Aspire
/// Dashboard / Jaeger / Prometheus'a gönderen kısım) yalnızca Api katmanında, bileşim kökünde kurulur. Böylece iş
/// mantığı hangi izleme aracının kullanıldığını bilmez ve SDK olmadan da çalışır (dinleyici yoksa maliyeti ~sıfır).
/// </summary>
public static class LlmLabTelemetry
{
    /// <summary>Uygulamanın kendi iz/metrik kaynağı (handler'lar, arama, yükleme).</summary>
    public const string SourceName = "LlmLab";

    /// <summary>Microsoft.Extensions.AI'ın model çağrılarını (gen_ai.* anlamsal kuralları) yayınladığı kaynak.</summary>
    public const string AiSourceName = "LlmLab.AI";

    /// <summary>Agent Framework'ün ajan çalıştırmalarını yayınladığı kaynak.</summary>
    public const string AgentSourceName = "LlmLab.Agent";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // ---- Sohbet ----
    public static readonly Counter<long> ChatTurns = Meter.CreateCounter<long>(
        "llmlab.chat.turns", description: "Tamamlanan sohbet turları (outcome: ok | failed | canceled).");

    public static readonly Histogram<double> ChatDuration = Meter.CreateHistogram<double>(
        "llmlab.chat.duration", unit: "s", description: "Bir sohbet turunun toplam süresi.");

    public static readonly Histogram<double> FirstTokenLatency = Meter.CreateHistogram<double>(
        "llmlab.chat.first_token", unit: "s", description: "Turun başlangıcından ilk cevap tokenine kadar geçen süre.");

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        "llmlab.agent.tool_calls", description: "Ajanın çağırdığı araçlar (tool: araç adı).");

    // ---- RAG ----
    public static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        "llmlab.rag.search.duration", unit: "s", description: "Hibrit belge aramasının süresi.");

    public static readonly Histogram<long> SearchResults = Meter.CreateHistogram<long>(
        "llmlab.rag.search.results", description: "Aramanın döndürdüğü parça sayısı.");

    // ---- Belgeler ----
    public static readonly Counter<long> DocumentsUploaded = Meter.CreateCounter<long>(
        "llmlab.documents.uploaded", description: "Başarıyla yüklenen belgeler.");

    public static readonly Histogram<long> DocumentChunks = Meter.CreateHistogram<long>(
        "llmlab.documents.chunks", description: "Yüklenen belgenin parça sayısı.");

    public static readonly Histogram<double> UploadDuration = Meter.CreateHistogram<double>(
        "llmlab.documents.upload.duration", unit: "s", description: "Belge yükleme hattının (çıkar, parçala, göm, kaydet) süresi.");

    // ---- Etiket (tag) adları: tek yerde, yazım hatasına karşı ----
    public static class Tags
    {
        public const string ConversationId = "llmlab.conversation.id";
        public const string Think = "llmlab.chat.think";
        public const string UseDocuments = "llmlab.chat.use_documents";
        public const string Sources = "llmlab.chat.sources";
        public const string ToolCallCount = "llmlab.chat.tool_calls";
        public const string AnswerChars = "llmlab.chat.answer_chars";
        public const string ThinkingChars = "llmlab.chat.thinking_chars";
        public const string ThinkingSeconds = "llmlab.chat.thinking_seconds";
        public const string FirstTokenSeconds = "llmlab.chat.first_token_seconds";
        public const string SessionSaved = "llmlab.chat.session_saved";
    }
}
