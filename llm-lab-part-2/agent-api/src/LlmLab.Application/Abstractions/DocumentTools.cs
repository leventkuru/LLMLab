using LlmLab.Domain.Conversations;

namespace LlmLab.Application.Abstractions;

/// <summary>
/// Belge MCP sunucusu ile ajan arasındaki sözleşme. Sunucu araçları bu adlarla yayınlar; ajan, `search_documents`
/// sonucunu özel işler (kaynakları numaralar, güvenli çerçeveye alır, arayüze kaynak listesi gönderir).
/// </summary>
public static class DocumentToolContract
{
    public const string SearchToolName = "search_documents";
    public const string ListToolName = "list_documents";
}

/// <summary>
/// `search_documents` aracının yapısal (structuredContent) sonucu. Sunucu yalnızca ilgili bulunan parçaları döndürür
/// (RAG eşikleri sunucudadır); numaralandırma ve modele sunuş biçimi istemci (ajan) tarafındadır, çünkü numaralar
/// bir turdaki birden çok aramada birikimlidir ve arayüzdeki kaynak listesiyle eşleşmelidir.
/// </summary>
public sealed record DocumentSearchResult(IReadOnlyList<SourceReference> Hits);
