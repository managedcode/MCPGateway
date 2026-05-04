using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private EmbeddingGeneratorLease ResolveEmbeddingGenerator()
    {
        if (
            _serviceProvider.GetService(typeof(IServiceScopeFactory))
            is not IServiceScopeFactory scopeFactory
        )
        {
            return new EmbeddingGeneratorLease(ResolveEmbeddingGenerator(_serviceProvider));
        }

        var scope = scopeFactory.CreateAsyncScope();
        var generator = ResolveEmbeddingGenerator(scope.ServiceProvider);
        return new EmbeddingGeneratorLease(generator, scope);
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? ResolveEmbeddingGenerator(
        IServiceProvider serviceProvider
    ) =>
        serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(
            McpGatewayServiceKeys.EmbeddingGenerator
        ) ?? serviceProvider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

    private ToolEmbeddingStoreLease ResolveToolEmbeddingStore()
    {
        if (
            _serviceProvider.GetService(typeof(IServiceScopeFactory))
            is not IServiceScopeFactory scopeFactory
        )
        {
            return new ToolEmbeddingStoreLease(
                _serviceProvider.GetService<IMcpGatewayToolEmbeddingStore>()
            );
        }

        var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IMcpGatewayToolEmbeddingStore>();
        return new ToolEmbeddingStoreLease(store, scope);
    }

    private static double CalculateCosine(
        ToolCatalogEntry entry,
        float[] queryVector,
        double queryMagnitude
    )
    {
        if (
            entry.Vector is null
            || entry.Magnitude <= double.Epsilon
            || queryMagnitude <= double.Epsilon
        )
        {
            return EmbeddingCosineUnavailableScore;
        }

        var overlap = Math.Min(entry.Vector.Length, queryVector.Length);
        if (overlap == 0)
        {
            return EmbeddingCosineUnavailableScore;
        }

        var dot = EmbeddingDotProductInitialValue;
        for (var index = 0; index < overlap; index++)
        {
            dot += entry.Vector[index] * queryVector[index];
        }

        return dot / (entry.Magnitude * queryMagnitude);
    }

    private static double CalculateMagnitude(IReadOnlyList<float> vector)
    {
        if (vector.Count == 0)
        {
            return ToolEmbeddingDefaultMagnitude;
        }

        var magnitudeSquared = EmbeddingMagnitudeSquaredInitialValue;
        foreach (var component in vector)
        {
            magnitudeSquared += component * component;
        }

        return Math.Sqrt(magnitudeSquared);
    }

    private static VectorTokenUsage ExtractVectorTokenUsage(
        UsageDetails? usage,
        IReadOnlyList<string>? inputs = null
    )
    {
        var estimatedTokenCount = inputs is null ? 0L : EstimateTokenCount(inputs);
        var inputTokenCount = Math.Max(0L, usage?.InputTokenCount ?? estimatedTokenCount);
        var totalTokenCount = Math.Max(inputTokenCount, usage?.TotalTokenCount ?? inputTokenCount);
        return new VectorTokenUsage(inputTokenCount, totalTokenCount);
    }

    private static long EstimateTokenCount(IReadOnlyList<string> inputs)
    {
        var tokenCount = 0L;
        foreach (var input in inputs)
        {
            tokenCount += EstimateTokenCount(input);
        }

        return tokenCount;
    }

    private static long EstimateTokenCount(string value) =>
        value
            .Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .LongLength;

    private static bool ApplyEmbedding(
        IList<ToolCatalogEntry> entries,
        int index,
        IReadOnlyList<float> vector,
        ref int vectorizedToolCount
    )
    {
        if (vector.Count == 0)
        {
            return false;
        }

        var normalizedVector = vector.ToArray();
        var magnitude = CalculateMagnitude(normalizedVector);
        entries[index] = entries[index] with { Vector = normalizedVector, Magnitude = magnitude };

        if (magnitude <= double.Epsilon)
        {
            return false;
        }

        vectorizedToolCount++;
        return true;
    }

    private static string ComputeDocumentHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool MatchesStoredEmbedding(
        McpGatewayToolEmbeddingLookup lookup,
        McpGatewayToolEmbedding embedding
    )
    {
        if (!string.Equals(embedding.ToolId, lookup.ToolId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(embedding.DocumentHash, lookup.DocumentHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (lookup.EmbeddingGeneratorFingerprint is null)
        {
            return true;
        }

        return string.Equals(
            embedding.EmbeddingGeneratorFingerprint,
            lookup.EmbeddingGeneratorFingerprint,
            StringComparison.Ordinal
        );
    }

    private static string? ResolveEmbeddingGeneratorFingerprint(
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator
    )
    {
        if (embeddingGenerator is null)
        {
            return null;
        }

        var metadata =
            embeddingGenerator.GetService(typeof(EmbeddingGeneratorMetadata))
            as EmbeddingGeneratorMetadata;
        var generatorTypeName =
            embeddingGenerator.GetType().FullName ?? embeddingGenerator.GetType().Name;

        return ComputeDocumentHash(
            string.Join(
                FingerprintComponentSeparator,
                metadata?.ProviderName ?? FingerprintUnknownComponent,
                metadata?.ProviderUri?.AbsoluteUri ?? FingerprintUnknownComponent,
                metadata?.DefaultModelId ?? FingerprintUnknownComponent,
                metadata?.DefaultModelDimensions?.ToString(CultureInfo.InvariantCulture)
                    ?? FingerprintUnknownComponent,
                generatorTypeName ?? FingerprintUnknownComponent
            )
        );
    }

    private string? GetOrCreateEmbeddingGeneratorFingerprint(
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator
    )
    {
        var cachedFingerprint = Volatile.Read(ref _embeddingGeneratorFingerprint);
        if (cachedFingerprint is not null)
        {
            return cachedFingerprint;
        }

        var resolvedFingerprint = ResolveEmbeddingGeneratorFingerprint(embeddingGenerator);
        if (resolvedFingerprint is null)
        {
            return null;
        }

        return Interlocked.CompareExchange(
                ref _embeddingGeneratorFingerprint,
                resolvedFingerprint,
                null
            ) ?? resolvedFingerprint;
    }

    private static string? ResolveSearchQueryChatClientFingerprint(IChatClient? chatClient)
    {
        if (chatClient is null)
        {
            return null;
        }

        var metadata = chatClient.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        var clientTypeName = chatClient.GetType().FullName ?? chatClient.GetType().Name;

        return ComputeDocumentHash(
            string.Join(
                FingerprintComponentSeparator,
                metadata?.ProviderName ?? FingerprintUnknownComponent,
                metadata?.ProviderUri?.AbsoluteUri ?? FingerprintUnknownComponent,
                metadata?.DefaultModelId ?? FingerprintUnknownComponent,
                clientTypeName ?? FingerprintUnknownComponent
            )
        );
    }

    private string? GetOrCreateSearchQueryChatClientFingerprint(IChatClient? chatClient)
    {
        var cachedFingerprint = Volatile.Read(ref _searchQueryChatClientFingerprint);
        if (cachedFingerprint is not null)
        {
            return cachedFingerprint;
        }

        var resolvedFingerprint = ResolveSearchQueryChatClientFingerprint(chatClient);
        if (resolvedFingerprint is null)
        {
            return null;
        }

        return Interlocked.CompareExchange(
                ref _searchQueryChatClientFingerprint,
                resolvedFingerprint,
                null
            ) ?? resolvedFingerprint;
    }

    private async Task<string?> ResolveSearchResultEmbeddingGeneratorFingerprintAsync(
        ToolCatalogSnapshot snapshot,
        SearchInput searchInput,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (
            !snapshot.HasVectors
            || !searchInput.HasTerms
            || _searchStrategy == McpGatewaySearchStrategy.Graph
        )
        {
            return null;
        }

        await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
        return GetOrCreateEmbeddingGeneratorFingerprint(embeddingGeneratorLease.Generator);
    }
}
