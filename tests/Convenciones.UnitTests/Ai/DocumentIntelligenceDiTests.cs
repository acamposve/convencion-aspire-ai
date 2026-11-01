using Convenciones.Ai.DocumentIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Ai;

public sealed class DocumentIntelligenceDiTests
{
    [Fact]
    public void The_typed_client_has_a_single_constructor_and_resolves()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureDocumentIntelligence(new AzureDocumentIntelligenceOptions
        {
            Endpoint = new Uri("https://example.cognitiveservices.azure.com/"),
            ApiKey = string.Join("-", "test", "value"),
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDocumentLayoutClient>().ShouldNotBeNull();
    }
}
