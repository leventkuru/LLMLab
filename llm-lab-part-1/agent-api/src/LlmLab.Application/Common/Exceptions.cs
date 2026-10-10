namespace LlmLab.Application.Common;

/// <summary>Uygulama katmanı hataları; API bunları uygun HTTP durum koduna çevirir.</summary>
public abstract class AppException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>404: istenen kayıt yok.</summary>
public sealed class NotFoundException(string message) : AppException(message, 404);

/// <summary>400: istek geçersiz.</summary>
public sealed class ValidationException(string message) : AppException(message, 400);

/// <summary>422: istek geçerli ama işlenemedi (ör. PDF'den metin çıkarılamadı).</summary>
public sealed class UnprocessableException(string message) : AppException(message, 422);
