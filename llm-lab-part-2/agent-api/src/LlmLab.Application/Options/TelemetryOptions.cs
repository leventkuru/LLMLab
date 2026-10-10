namespace LlmLab.Application.Options;

/// <summary>
/// Gözlemlenebilirlik ayarları ("Telemetry" bölümü). Standart OTEL_* ortam değişkenleri de çalışır
/// (ör. OTEL_EXPORTER_OTLP_ENDPOINT), bu bölüm yalnızca proje düzeyindeki anahtarlardır.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>false = hiçbir telemetri kurulmaz (uygulama etkilenmez).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>İzlerde ve metriklerde görünen servis adı.</summary>
    public string ServiceName { get; set; } = "LlmLab.Api";

    /// <summary>
    /// OTLP toplayıcı adresi (ör. Aspire Dashboard: http://localhost:4317). Boşsa dışarı gönderim yapılmaz.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>İzleri/metrikleri/günlükleri konsola da yaz (denemek için; üretimde kapalı tutun).</summary>
    public bool Console { get; set; }

    /// <summary>
    /// Kullanıcı mesajları, model cevapları ve belge metinleri izlere yazılsın mı? Varsayılan KAPALI: bunlar kişisel /
    /// gizli veri içerebilir ve izleme sistemlerine kopyalanmamalıdır. Yerel geliştirmede, ne olduğunu görmek için açılır.
    /// </summary>
    public bool CaptureSensitiveData { get; set; }
}
