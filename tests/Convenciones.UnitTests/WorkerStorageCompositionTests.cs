using Convenciones.Documents;
using Convenciones.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

public sealed class WorkerStorageCompositionTests
{
    [Fact]
    public void Storage_typed_client_gets_the_supabase_base_address()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAi:Endpoint"] = "https://example.cognitiveservices.azure.com/",
                ["AzureAi:Deployment"] = "clauses-mistral",
                ["AzureAi:ApiKey"] = string.Join("-", "test", "value"),
                ["DocumentIntelligence:Endpoint"] = "https://example.cognitiveservices.azure.com/",
                ["DocumentIntelligence:ApiKey"] = string.Join("-", "test", "value"),
                ["Documents:SupabaseUrl"] = "https://project.supabase.co",
                ["Documents:SupabaseSecretKey"] = string.Join("-", "test", "value"),
                ["Documents:Bucket"] = "documents",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        WorkerComposition.AddProcessingStage(services, configuration, "Host=localhost;Database=test;Username=test;Password=test");

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IDocumentStorage>().ShouldBeOfType<SupabaseDocumentStorage>();

        storage.BaseAddress.ShouldBe(new Uri("https://project.supabase.co/storage/v1/"));
    }
}
