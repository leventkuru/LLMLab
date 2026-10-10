using LlmLab.Api.Contracts;
using LlmLab.Api.Streaming;
using LlmLab.Application.Features.Chat;
using LlmLab.Application.Features.Conversations;
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
            var command = new StreamChatCommand(req.Message, req.ConversationId, req.Think, req.UseDocuments);
            await foreach (var evt in handler.HandleAsync(command, ctx.RequestAborted))
                await SseWriter.WriteAsync(ctx.Response, evt, ctx.RequestAborted);
        }).WithTags("Chat");
    }
}
