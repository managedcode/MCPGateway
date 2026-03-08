namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayInMemoryToolEmbeddingStoreTests
{
    [TUnit.Core.Test]
    public async Task GetAsync_ReturnsEmbeddingsForMatchingLookups()
    {
        using var store = await CreateStoreAsync(
            CreateEmbedding("local:github_search_issues", "github_search_issues", "hash-1", "fingerprint-a", [1f, 2f, 3f]),
            CreateEmbedding("local:weather_search_forecast", "weather_search_forecast", "hash-2", "fingerprint-a", [4f, 5f, 6f]));

        var result = await store.GetAsync(
        [
            CreateLookup("local:weather_search_forecast", "hash-2", "fingerprint-a"),
            CreateLookup("local:missing", "hash-3", "fingerprint-a"),
            CreateLookup("local:github_search_issues", "hash-1", "fingerprint-a")
        ]);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(static item => item.ToolId == "local:github_search_issues")).IsTrue();
        await Assert.That(result.Any(static item => item.ToolId == "local:weather_search_forecast")).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task UpsertAsync_ClonesVectorsOnWriteAndRead()
    {
        using var store = new McpGatewayInMemoryToolEmbeddingStore();
        var inputVector = new[] { 1f, 2f, 3f };

        await store.UpsertAsync([CreateEmbedding("local:github_search_issues", "github_search_issues", "hash-1", "fingerprint-a", inputVector)]);

        inputVector[0] = 99f;

        var firstRead = await store.GetAsync(
        [
            CreateLookup("local:github_search_issues", "hash-1", "fingerprint-a")
        ]);
        firstRead[0].Vector[1] = 77f;

        var secondRead = await store.GetAsync(
        [
            CreateLookup("local:github_search_issues", "hash-1", "fingerprint-a")
        ]);

        await Assert.That(secondRead[0].Vector[0]).IsEqualTo(1f);
        await Assert.That(secondRead[0].Vector[1]).IsEqualTo(2f);
        await Assert.That(secondRead[0].Vector[2]).IsEqualTo(3f);
    }

    [TUnit.Core.Test]
    public async Task GetAsync_TreatsToolIdsCaseInsensitivelyAndSupportsFingerprintFallback()
    {
        using var store = await CreateStoreAsync(
            CreateEmbedding("local:github_search_issues", "github_search_issues", "hash-1", "fingerprint-a", [1f, 2f, 3f]));

        var fingerprintMatch = await store.GetAsync(
        [
            CreateLookup("LOCAL:GITHUB_SEARCH_ISSUES", "hash-1", "fingerprint-a")
        ]);
        var fingerprintAgnosticMatch = await store.GetAsync(
        [
            CreateLookup("LOCAL:GITHUB_SEARCH_ISSUES", "hash-1")
        ]);

        await Assert.That(fingerprintMatch.Count).IsEqualTo(1);
        await Assert.That(fingerprintAgnosticMatch.Count).IsEqualTo(1);
        await Assert.That(fingerprintAgnosticMatch[0].ToolId).IsEqualTo("local:github_search_issues");
    }

    [TUnit.Core.Test]
    public async Task GetAsync_WithoutFingerprintReturnsMostRecentlyUpsertedEmbeddingForDocument()
    {
        using var store = new McpGatewayInMemoryToolEmbeddingStore();

        await store.UpsertAsync(
        [
            CreateEmbedding("local:github_search_issues", "github_search_issues", "hash-1", "fingerprint-a", [1f, 2f, 3f]),
            CreateEmbedding("local:github_search_issues", "github_search_issues", "hash-1", "fingerprint-b", [9f, 8f, 7f])
        ]);

        var result = await store.GetAsync(
        [
            CreateLookup("LOCAL:GITHUB_SEARCH_ISSUES", "hash-1")
        ]);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].EmbeddingGeneratorFingerprint).IsEqualTo("fingerprint-b");
        await Assert.That(result[0].Vector[0]).IsEqualTo(9f);
    }

    private static async Task<McpGatewayInMemoryToolEmbeddingStore> CreateStoreAsync(params McpGatewayToolEmbedding[] embeddings)
    {
        var store = new McpGatewayInMemoryToolEmbeddingStore();
        await store.UpsertAsync(embeddings);
        return store;
    }

    private static McpGatewayToolEmbedding CreateEmbedding(
        string toolId,
        string toolName,
        string documentHash,
        string fingerprint,
        float[] vector)
        => new(
            toolId,
            "local",
            toolName,
            documentHash,
            fingerprint,
            vector);

    private static McpGatewayToolEmbeddingLookup CreateLookup(
        string toolId,
        string documentHash,
        string? fingerprint = null)
        => new(toolId, documentHash, fingerprint);
}
