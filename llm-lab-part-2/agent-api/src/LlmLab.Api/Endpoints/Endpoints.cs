using LlmLab.Api.Contracts;
using LlmLab.Api.Streaming;
using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Agents;
using LlmLab.Application.Features.Chat;
using LlmLab.Application.Features.Conversations;
using LlmLab.Application.Features.Dialogs;
using LlmLab.Application.Features.Documents;

namespace LlmLab.Api.Endpoints;

/// <summary>
/// Uç noktalar ince tutulur: HTTP isteğini komuta çevirir, handler'ı çağırır, sonucu döndürür.
/// İş mantığı, doğrulama ve hata yönetimi burada değil Application katmanındadır.
/// </summary>
internal static class Endpoints
{
    public static IEndpointRouteBuilder MapLlmLabEndpoints(this IEndpointRouteBuilder app)
    {
        MapConversations(app);
        MapDocuments(app);
        MapChat(app);
        MapAgents(app);
        MapDialogs(app);
        MapSubAgents(app);
        return app;
    }

    private static void MapConversations(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/conversations").WithTags("Conversations");

        g.MapGet("/", (ListConversationsHandler h, CancellationToken ct) => h.HandleAsync(ct));

        g.MapGet("/{id:guid}", (Guid id, GetConversationHandler h, CancellationToken ct) => h.HandleAsync(id, ct));

        g.MapDelete("/{id:guid}", async (Guid id, DeleteConversationHandler h, CancellationToken ct) =>
        {
            await h.HandleAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static void MapDocuments(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/documents").WithTags("Documents");

        g.MapGet("/", (ListDocumentsHandler h, CancellationToken ct) => h.HandleAsync(ct));

        // Form dosyası için antiforgery belirteci istenir; bu API çerez kullanmadığı için kapatıldı.
        g.MapPost("/", async (IFormFile file, UploadDocumentHandler h, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var created = await h.HandleAsync(new UploadDocumentCommand(file.FileName, ms.ToArray()), ct);
            return Results.Created($"/documents/{created.Id}", created);
        }).DisableAntiforgery();

        g.MapDelete("/{id:guid}", async (Guid id, DeleteDocumentHandler h, CancellationToken ct) =>
        {
            await h.HandleAsync(id, ct);
            return Results.NoContent();
        });

        app.MapPost("/search", async (SearchRequest req, SearchDocumentsHandler h, CancellationToken ct) =>
            Results.Ok(await h.HandleAsync(new SearchDocumentsQuery(req.Query, req.TopK), ct)))
            .WithTags("Documents");
    }

    private static void MapChat(IEndpointRouteBuilder app)
    {
        app.MapPost("/chat/stream", async (ChatRequest req, StreamChatHandler handler, HttpContext ctx) =>
        {
            var command = new StreamChatCommand(req.Message, req.ConversationId, req.Think, req.UseDocuments, req.AgentId);
            await foreach (var evt in handler.HandleAsync(command, ctx.RequestAborted))
                await SseWriter.WriteAsync(ctx.Response, evt, ctx.RequestAborted);
        }).WithTags("Chat");
    }

    /// <summary>Ajan kayıt defteri: ajanları listele, oluştur/güncelle, "çalışsın" anahtarı, sil; araç envanteri.</summary>
    private static void MapAgents(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/agents").WithTags("Agents");

        g.MapGet("/", (ListAgentsHandler h, CancellationToken ct) => h.HandleAsync(ct));

        g.MapGet("/{id}", (string id, GetAgentHandler h, CancellationToken ct) => h.HandleAsync(id, ct));

        g.MapPut("/{id}", async (string id, SaveAgentRequest req, SaveAgentHandler h, CancellationToken ct) =>
            Results.Ok(await h.HandleAsync(
                new SaveAgentCommand(id, req.DisplayName, req.Description, req.Instructions, req.AllowedTools,
                    req.CanDelegate, req.Hosting, req.Enabled), ct)));

        g.MapPatch("/{id}/enabled", async (string id, SetEnabledRequest req, SetAgentEnabledHandler h, CancellationToken ct) =>
            Results.Ok(await h.HandleAsync(id, req.Enabled, ct)));

        g.MapDelete("/{id}", async (string id, DeleteAgentHandler h, CancellationToken ct) =>
        {
            await h.HandleAsync(id, ct);
            return Results.NoContent();
        });

        // Ajana verilebilecek araçlar (ayakta olan MCP sunucularından keşfedilen).
        app.MapGet("/tools", (ListToolsHandler h, CancellationToken ct) => h.HandleAsync(ct)).WithTags("Agents");
    }

    private static void MapDialogs(IEndpointRouteBuilder app)
    {
        app.MapPost("/dialogs/stream", async (DialogRequest req, DialogHandler handler, HttpContext ctx) =>
        {
            var command = new DialogCommand(req.AgentA, req.AgentB, req.Topic, req.Turns, req.UseDocuments);
            await foreach (var evt in handler.HandleAsync(command, ctx.RequestAborted))
                await SseWriter.WriteDialogAsync(ctx.Response, evt, ctx.RequestAborted);
        }).WithTags("Dialogs");
    }

    /// <summary>Koordinatörün başlattığı alt ajan görevlerini görme ve durdurma (izleme / elle müdahale).</summary>
    private static void MapSubAgents(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/subagents").WithTags("SubAgents");

        g.MapGet("/", (ISubAgentRunner runner) => runner.List());

        g.MapGet("/{taskId}", (string taskId, ISubAgentRunner runner) =>
            runner.Get(taskId) is { } snap ? Results.Ok(snap) : Results.NotFound());

        g.MapPost("/{taskId}/stop", (string taskId, ISubAgentRunner runner) =>
            runner.Stop(taskId) ? Results.NoContent() : Results.NotFound());
    }
}
