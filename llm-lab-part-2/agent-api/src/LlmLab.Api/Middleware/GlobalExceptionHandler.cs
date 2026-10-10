using LlmLab.Application.Common;
using LlmLab.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LlmLab.Api.Middleware;

/// <summary>
/// Tek hata kapısı: uygulama/etki alanı hatalarını uygun HTTP durum koduna, beklenmeyenleri 500'e çevirir.
/// Uç noktalar try/catch yazmaz. 500'de iç ayrıntı istemciye sızdırılmaz, günlüğe yazılır.
/// Yanıt, ProblemDetails (RFC 9457) biçimindedir; eski arayüzle uyum için ayrıca "error" alanı taşır.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // İstemci bağlantıyı kendisi kestiyse (Durdur) bu bir hata değildir.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return true;

        var (status, message) = exception switch
        {
            AppException e => (e.StatusCode, e.Message),
            DomainException e => (StatusCodes.Status400BadRequest, e.Message),
            BadHttpRequestException e => (e.StatusCode, e.Message),
            _ => (StatusCodes.Status500InternalServerError, "Beklenmeyen bir hata oluştu."),
        };

        if (status >= 500) log.LogError(exception, "İşlenemeyen hata: {Path}", context.Request.Path);
        else log.LogWarning("İstek reddedildi ({Status}): {Message}", status, message);

        // Cevap zaten akmaya başladıysa (SSE ortasında) durum kodu değiştirilemez; yalnızca günlüğe yazıldı.
        if (context.Response.HasStarted) return true;

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = message,
            Extensions = { ["error"] = message },
        }, ct);
        return true;
    }
}
