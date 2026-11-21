using Convenciones.Ai;
using Convenciones.Ai.DocumentIntelligence;
using Convenciones.Documents;
using Convenciones.Worker.Extraction;
using Convenciones.Worker.Financial;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Convenciones.Worker;

/// <summary>
/// Wires the real extraction stage when the AI endpoints are configured; otherwise the worker keeps
/// the explicit "not configured" stage so a missing setting fails visibly instead of processing nothing.
/// </summary>
public static class WorkerComposition
{
    public static void AddProcessingStage(
        IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        var aiEndpoint = configuration["AzureAi:Endpoint"];
        var documentEndpoint = configuration["DocumentIntelligence:Endpoint"];
        var apiKey = configuration["AzureAi:ApiKey"];

        if (string.IsNullOrWhiteSpace(aiEndpoint)
            || string.IsNullOrWhiteSpace(documentEndpoint)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IProcessingStage, UnavailableProcessingStage>();
            return;
        }

        services.AddAzureAi(new AzureAiOptions
        {
            Endpoint = new Uri(aiEndpoint),
            Deployment = configuration["AzureAi:Deployment"] ?? "clauses",
            ApiKey = apiKey,
            ApiVersion = configuration["AzureAi:ApiVersion"] ?? "2024-05-01-preview",
            TimeoutSeconds = ReadInt(configuration, "AzureAi:TimeoutSeconds", 90),
            UseJsonSchema = ReadBool(configuration, "AzureAi:UseJsonSchema", false),
        });

        services.AddAzureDocumentIntelligence(new AzureDocumentIntelligenceOptions
        {
            Endpoint = new Uri(documentEndpoint),
            ApiKey = configuration["DocumentIntelligence:ApiKey"],
            TenantId = configuration["DocumentIntelligence:TenantId"],
            ClientId = configuration["DocumentIntelligence:ClientId"],
            ClientSecret = configuration["DocumentIntelligence:ClientSecret"],
            ApiVersion = configuration["DocumentIntelligence:ApiVersion"] ?? "2024-11-30",
            ModelId = configuration["DocumentIntelligence:ModelId"] ?? "prebuilt-layout",
            TimeoutSeconds = ReadInt(configuration, "DocumentIntelligence:TimeoutSeconds", 90),
            PollIntervalMilliseconds = ReadInt(configuration, "DocumentIntelligence:PollIntervalMilliseconds", 1000),
            MinCharactersPerPage = ReadInt(configuration, "DocumentIntelligence:MinCharactersPerPage", 50),
        });

        if (!string.IsNullOrWhiteSpace(configuration["Documents:SupabaseUrl"])
            && !string.IsNullOrWhiteSpace(configuration["Documents:SupabaseSecretKey"])
            && !string.IsNullOrWhiteSpace(configuration["Documents:Bucket"]))
        {
            var supabaseUrl = configuration["Documents:SupabaseUrl"]!;
            var supabaseSecretKey = configuration["Documents:SupabaseSecretKey"]!;
            services.AddHttpClient<IDocumentStorage, SupabaseDocumentStorage>(client =>
            {
                client.BaseAddress = new Uri($"{supabaseUrl.TrimEnd('/')}/storage/v1/");
                client.DefaultRequestHeaders.Add("apikey", supabaseSecretKey);
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", supabaseSecretKey);
            });
        }
        else
        {
            services.AddSingleton<IDocumentStorage>(
                new LocalDocumentStorage(configuration["Documents:StorageRoot"] ?? "/tmp/documents"));
        }

        services.AddSingleton<IClauseStore>(new NpgsqlClauseStore(connectionString));
        var financialBudget = new FinancialBudgetStore(connectionString);
        services.AddSingleton(financialBudget);
        services.AddSingleton<IFinancialBudget>(financialBudget);
        services.AddSingleton(new ExtractionOptions
        {
            ChunkCharacters = ReadInt(configuration, "Extraction:ChunkCharacters", 10_000),
            OverlapCharacters = ReadInt(configuration, "Extraction:OverlapCharacters", 1_000),
            MaxChunks = ReadInt(configuration, "Extraction:MaxChunks", 30),
            MaxOutputTokens = ReadInt(configuration, "Extraction:MaxOutputTokens", 8_000),
            PromptVersion = configuration["Extraction:PromptVersion"] ?? "extraction/v1",
            Deployment = configuration["Extraction:Deployment"],
        });
        services.AddSingleton(new ClassificationOptions
        {
            BatchSize = ReadInt(configuration, "Classification:BatchSize", 20),
            MaxOutputTokens = ReadInt(configuration, "Classification:MaxOutputTokens", 4_000),
            PromptVersion = configuration["Classification:PromptVersion"] ?? "classification/v1",
            Deployment = configuration["Classification:Deployment"],
        });
        services.AddSingleton<ClauseExtractionStage>();
        services.AddSingleton<ClauseClassificationStage>();
        services.AddSingleton<IProcessingStage, ClauseProcessingStage>();
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;
}
