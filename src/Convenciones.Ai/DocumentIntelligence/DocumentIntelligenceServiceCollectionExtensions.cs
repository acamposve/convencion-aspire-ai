using Microsoft.Extensions.DependencyInjection;

namespace Convenciones.Ai.DocumentIntelligence;

public static class DocumentIntelligenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Document Intelligence client. Entra is preferred; an API key is accepted only as a
    /// fallback, and configuration is validated at startup.
    /// </summary>
    public static IServiceCollection AddAzureDocumentIntelligence(
        this IServiceCollection services,
        AzureDocumentIntelligenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        services.AddSingleton(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            services.AddSingleton<IAccessTokenProvider>(
                new ClientSecretAccessTokenProvider(
                    options.TenantId!,
                    options.ClientId!,
                    options.ClientSecret!));
        }

        services.AddHttpClient<IDocumentLayoutClient, AzureDocumentIntelligenceClient>(
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 10));

        return services;
    }
}
