namespace LlmLab.Api.Contracts;

/// <summary>POST /chat/stream gövdesi.</summary>
/// <param name="Think">Modelin cevaptan önce "düşünmesine" izin ver.</param>
/// <param name="ConversationId">Boşsa yeni sohbet (ve yeni ajan oturumu) başlar.</param>
/// <param name="UseDocuments">Doğruysa ajana belge arama aracı verilir.</param>
public sealed record ChatRequest(string Message, bool Think = true, Guid? ConversationId = null, bool UseDocuments = false);

/// <summary>POST /search gövdesi.</summary>
public sealed record SearchRequest(string Query, int? TopK);
