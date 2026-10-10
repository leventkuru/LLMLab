using LlmLab.Application.Abstractions;
using LlmLab.Application.Features.Agents;
using LlmLab.Application.Features.Chat;
using LlmLab.Application.Features.Dialogs;
using LlmLab.Application.Features.Conversations;
using LlmLab.Application.Features.Documents;
using LlmLab.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmLab.Application;

public static class DependencyInjection
{
    /// <summary>Uygulama katmanının kayıtları: seçenekler (başlangıçta doğrulanır) ve kullanım senaryoları.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        // ValidateOnStart: yanlış bir ayar, ilk istekte değil uygulama açılırken patlar.
        services.AddOptions<RagOptions>().Bind(configuration.GetSection(RagOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AgentSettings>().Bind(configuration.GetSection(AgentSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<TelemetryOptions>().Bind(configuration.GetSection(TelemetryOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IDocumentSearch, HybridDocumentSearch>();

        // Kullanım senaryoları (handler'lar)
        services.AddScoped<ListConversationsHandler>();
        services.AddScoped<GetConversationHandler>();
        services.AddScoped<DeleteConversationHandler>();
        services.AddScoped<UploadDocumentHandler>();
        services.AddScoped<ListDocumentsHandler>();
        services.AddScoped<DeleteDocumentHandler>();
        services.AddScoped<SearchDocumentsHandler>();
        services.AddScoped<StreamChatHandler>();
        services.AddScoped<DialogHandler>();

        // Ajan kayıt defteri
        services.AddScoped<ListAgentsHandler>();
        services.AddScoped<GetAgentHandler>();
        services.AddScoped<SaveAgentHandler>();
        services.AddScoped<SetAgentEnabledHandler>();
        services.AddScoped<DeleteAgentHandler>();
        services.AddScoped<ListToolsHandler>();

        return services;
    }
}
