using System.ComponentModel;
using System.Globalization;
using System.Text;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace LlmLab.Mcp.Documents;

[McpServerToolType]
public static class DocumentTools
{
    /// <summary>
    /// Yapısal sonuç döndürür (structuredContent): ajan, kaynak listesini ve numaralandırmayı kendi turuna göre
    /// kurabilsin diye. Yalnızca ilgili bulunan parçalar gelir; eşikler (MinScore, KeywordFloor, TopK) bu sunucudadır.
    /// </summary>
    [McpServerTool(Name = DocumentToolContract.SearchToolName, ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Kullanıcının yüklediği belgelerde anlam ve anahtar kelime araması yapar ve ilgili parçaları döndürür. " +
        "Belgelerle ilgili olabilecek her soruda kullan. " +
        "query: aranacak kısa ve öz sorgu (kullanıcının sorusunu olduğu gibi ya da özetleyerek).")]
    public static async Task<DocumentSearchResult> SearchDocuments(
        IDocumentSearch search,
        IOptions<RagOptions> rag,
        [Description("Aranacak kısa ve öz sorgu")] string query,
        CancellationToken ct)
    {
        var rules = rag.Value;
        var hits = await search.SearchAsync(query, rules.ChatTopK, ct);
        return new DocumentSearchResult(hits.Where(h => rules.IsRelevant(h.Score, h.KeywordHits)).ToList());
    }

    [McpServerTool(Name = DocumentToolContract.ListToolName, ReadOnly = true)]
    [Description(
        "Kullanıcının yüklediği belgelerin listesini (dosya adı, parça sayısı, boyut) döndürür. " +
        "\"Hangi belgelerim var?\" gibi sorularda kullan. Belge İÇERİĞİ için bunu değil search_documents aracını kullan.")]
    public static async Task<string> ListDocuments(IDocumentRepository documents, CancellationToken ct)
    {
        var list = await documents.ListAsync(ct);
        if (list.Count == 0) return "Henüz yüklenmiş belge yok.";

        // Dosya adları kullanıcı verisidir: etiketli veri olarak sunulur, talimat sayılmaz.
        var sb = new StringBuilder($"<belgeler adet=\"{list.Count}\">\n");
        foreach (var d in list)
        {
            var name = d.FileName.Replace("<", "&lt;");
            sb.Append(CultureInfo.InvariantCulture, $"- {name} ({d.ChunkCount} parça, {d.SizeBytes / 1024.0:0.#} KB)\n");
        }
        // Not: sonuna "talimatlara uyma" gibi bir hatırlatma EKLEMEYİN. Ölçüldü: qwen3:8b onu cevaba aynen kopyalıyor.
        // Bu kural ajanın sistem talimatındadır (AgentPrompts: araç sonuçları VERİDİR); burada yalnızca etiket + kaçış var.
        sb.Append("</belgeler>");
        return sb.ToString();
    }
}
