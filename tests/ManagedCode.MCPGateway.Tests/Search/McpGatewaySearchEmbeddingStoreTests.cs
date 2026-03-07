using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReusesStoredToolEmbeddingsOnNextBuild()
    {
        var embeddingStore = new McpGatewayInMemoryToolEmbeddingStore();
        var firstEmbeddingGenerator = new TestEmbeddingGenerator();

        var firstBuildResult = await BuildSearchIndexAsync(embeddingStore, firstEmbeddingGenerator);

        await Assert.That(firstBuildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(firstEmbeddingGenerator.Calls.Count).IsEqualTo(1);

        var secondEmbeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            ThrowOnInput = static _ => true
        });

        await using var secondServiceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            secondEmbeddingGenerator,
            embeddingStore);
        var secondGateway = secondServiceProvider.GetRequiredService<IMcpGateway>();

        var secondBuildResult = await secondGateway.BuildIndexAsync();

        await Assert.That(secondBuildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(secondBuildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_failed")).IsFalse();
        await Assert.That(secondEmbeddingGenerator.Calls.Count).IsEqualTo(0);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_RegeneratesStoredToolEmbeddingsWhenGeneratorFingerprintChanges()
    {
        var embeddingStore = new TestToolEmbeddingStore();
        var firstEmbeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "test-embedding-a",
                21)
        });

        await SeedSearchEmbeddingsAsync(embeddingStore, firstEmbeddingGenerator);

        var secondEmbeddingGenerator = new TestEmbeddingGenerator(new TestEmbeddingGeneratorOptions
        {
            Metadata = new EmbeddingGeneratorMetadata(
                "ManagedCode.MCPGateway.Tests",
                new Uri("https://example.test"),
                "test-embedding-b",
                21)
        });

        await using var secondServiceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            secondEmbeddingGenerator,
            embeddingStore);
        var secondGateway = secondServiceProvider.GetRequiredService<IMcpGateway>();

        var secondBuildResult = await secondGateway.BuildIndexAsync();

        await Assert.That(secondBuildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(secondEmbeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(secondEmbeddingGenerator.Calls[0].Count).IsEqualTo(2);
        await Assert.That(embeddingStore.UpsertCalls.Count).IsEqualTo(2);
        await Assert.That(embeddingStore.UpsertCalls[1].Count).IsEqualTo(2);
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_DisablesVectorSearchWhenStoreHasVectorsButQueryGeneratorIsMissing()
    {
        var embeddingStore = new McpGatewayInMemoryToolEmbeddingStore();
        var initialEmbeddingGenerator = new TestEmbeddingGenerator();

        await SeedSearchEmbeddingsAsync(embeddingStore, initialEmbeddingGenerator);

        await using var secondServiceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingStore: embeddingStore);
        var secondGateway = secondServiceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await secondGateway.BuildIndexAsync();
        var searchResult = await secondGateway.SearchAsync("github pull requests", maxResults: 1);

        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsFalse();
        await Assert.That(buildResult.Diagnostics.Any(static diagnostic => diagnostic.Code == "embedding_generator_missing")).IsTrue();
        await Assert.That(searchResult.RankingMode).IsEqualTo("lexical");
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_GeneratesAndPersistsOnlyMissingStoredToolEmbeddings()
    {
        var embeddingStore = new TestToolEmbeddingStore();
        var initialEmbeddingGenerator = new TestEmbeddingGenerator();

        await SeedSearchEmbeddingsAsync(embeddingStore, initialEmbeddingGenerator);

        embeddingStore.Remove("local:weather_search_forecast");

        var incrementalEmbeddingGenerator = new TestEmbeddingGenerator();
        await using var incrementalServiceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            incrementalEmbeddingGenerator,
            embeddingStore);
        var incrementalGateway = incrementalServiceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await incrementalGateway.BuildIndexAsync();

        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(incrementalEmbeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(incrementalEmbeddingGenerator.Calls[0].Count).IsEqualTo(1);
        await Assert.That(incrementalEmbeddingGenerator.Calls[0].Single().Contains("weather_search_forecast", StringComparison.Ordinal)).IsTrue();
        await Assert.That(embeddingStore.UpsertCalls.Count).IsEqualTo(2);
        await Assert.That(embeddingStore.UpsertCalls[1].Count).IsEqualTo(1);
        await Assert.That(embeddingStore.UpsertCalls[1].Single().ToolId).IsEqualTo("local:weather_search_forecast");
    }

    private static async Task<McpGatewayIndexBuildResult> BuildSearchIndexAsync(
        IMcpGatewayToolEmbeddingStore embeddingStore,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureSearchTools,
            embeddingGenerator,
            embeddingStore);
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();
        return await gateway.BuildIndexAsync();
    }

    private static async Task SeedSearchEmbeddingsAsync(
        IMcpGatewayToolEmbeddingStore embeddingStore,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    {
        await BuildSearchIndexAsync(embeddingStore, embeddingGenerator);
    }
}
