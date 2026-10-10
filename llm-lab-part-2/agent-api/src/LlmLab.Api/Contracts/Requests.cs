using LlmLab.Domain.Agents;

namespace LlmLab.Api.Contracts;

/// <summary>POST /chat/stream gövdesi.</summary>
/// <param name="Think">Modelin cevaptan önce "düşünmesine" izin ver.</param>
/// <param name="ConversationId">Boşsa yeni sohbet (ve yeni ajan oturumu) başlar.</param>
/// <param name="UseDocuments">Doğruysa ajana belge araçları verilir (ajanın izin listesi izin verirse).</param>
/// <param name="AgentId">Yeni sohbette kullanılacak ajan (kayıt defteri kimliği). Var olan sohbette yok sayılır.</param>
public sealed record ChatRequest(
    string Message, bool Think = true, Guid? ConversationId = null, bool UseDocuments = false, string? AgentId = null);

/// <summary>POST /search gövdesi.</summary>
public sealed record SearchRequest(string Query, int? TopK);

/// <summary>PUT /agents/{id} gövdesi (kimlik URL'den gelir).</summary>
public sealed record SaveAgentRequest(
    string DisplayName, string? Description = null, string? Instructions = null,
    IReadOnlyList<string>? AllowedTools = null, bool CanDelegate = false, AgentHosting Hosting = AgentHosting.Local,
    bool Enabled = true);

/// <summary>PATCH /agents/{id}/enabled gövdesi.</summary>
public sealed record SetEnabledRequest(bool Enabled);

/// <summary>POST /dialogs/stream gövdesi.</summary>
public sealed record DialogRequest(string AgentA, string AgentB, string Topic, int Turns = 6, bool UseDocuments = false);
