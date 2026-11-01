using Microsoft.Extensions.DependencyInjection;

namespace Convenciones.Ai;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure AI Foundry client. Configuration is validated here, at startup, so a missing
    /// endpoint or credential fails before the first document is processed.
    /// </summary>
    public static IServiceCollection AddAzureAi(this IServiceCollection services, AzureAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        services.AddSingleton(options);
        services.AddHttpClient<IAiModelClient, AzureFoundryAiModelClient>(
            client => client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 10));

        return services;
    }
}
