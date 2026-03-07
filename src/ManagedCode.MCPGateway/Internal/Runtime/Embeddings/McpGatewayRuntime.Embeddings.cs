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
        if (_serviceProvider.GetService(typeof(IServiceScopeFactory)) is not IServiceScopeFactory scopeFactory)
        {
            return new EmbeddingGeneratorLease(ResolveEmbeddingGenerator(_serviceProvider));
        }

        var scope = scopeFactory.CreateAsyncScope();
        var generator = ResolveEmbeddingGenerator(scope.ServiceProvider);
        return new EmbeddingGeneratorLease(generator, scope);
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? ResolveEmbeddingGenerator(IServiceProvider serviceProvider)
        => serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(McpGatewayServiceKeys.EmbeddingGenerator)
            ?? serviceProvider.GetService<IEmbeddingGenerator<string, Embedding<float>>>();

    private ToolEmbeddingStoreLease ResolveToolEmbeddingStore()
    {
        if (_serviceProvider.GetService(typeof(IServiceScopeFactory)) is not IServiceScopeFactory scopeFactory)
        {
            return new ToolEmbeddingStoreLease(_serviceProvider.GetService<IMcpGatewayToolEmbeddingStore>());
        }

        var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IMcpGatewayToolEmbeddingStore>();
        return new ToolEmbeddingStoreLease(store, scope);
    }

    private static double CalculateCosine(ToolCatalogEntry entry, float[] queryVector, double queryMagnitude)
    {
        if (entry.Vector is null || entry.Magnitude <= double.Epsilon || queryMagnitude <= double.Epsilon)
        {
            return 0d;
        }

        var overlap = Math.Min(entry.Vector.Length, queryVector.Length);
        if (overlap == 0)
        {
            return 0d;
        }

        var dot = 0d;
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
            return 0d;
        }

        var magnitudeSquared = 0d;
        foreach (var component in vector)
        {
            magnitudeSquared += component * component;
        }

        return Math.Sqrt(magnitudeSquared);
    }

    private static bool ApplyEmbedding(
        IList<ToolCatalogEntry> entries,
        int index,
        IReadOnlyList<float> vector,
        ref int vectorizedToolCount)
    {
        if (vector.Count == 0)
        {
            return false;
        }

        var normalizedVector = vector.ToArray();
        var magnitude = CalculateMagnitude(normalizedVector);
        entries[index] = entries[index] with
        {
            Vector = normalizedVector,
            Magnitude = magnitude
        };

        if (magnitude <= double.Epsilon)
        {
            return false;
        }

        vectorizedToolCount++;
        return true;
    }

    private static string ComputeDocumentHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool MatchesStoredEmbedding(
        McpGatewayToolEmbeddingLookup lookup,
        McpGatewayToolEmbedding embedding)
        => string.Equals(embedding.ToolId, lookup.ToolId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(embedding.DocumentHash, lookup.DocumentHash, StringComparison.Ordinal)
            && (lookup.EmbeddingGeneratorFingerprint is null
                || string.Equals(
                    embedding.EmbeddingGeneratorFingerprint,
                    lookup.EmbeddingGeneratorFingerprint,
                    StringComparison.Ordinal));

    private static string? ResolveEmbeddingGeneratorFingerprint(
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator)
    {
        if (embeddingGenerator is null)
        {
            return null;
        }

        var metadata = embeddingGenerator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
        var generatorTypeName = embeddingGenerator.GetType().FullName ?? embeddingGenerator.GetType().Name;

        return ComputeDocumentHash(string.Join(
            EmbeddingGeneratorFingerprintComponentSeparator,
            metadata?.ProviderName ?? EmbeddingGeneratorFingerprintUnknownComponent,
            metadata?.ProviderUri?.AbsoluteUri ?? EmbeddingGeneratorFingerprintUnknownComponent,
            metadata?.DefaultModelId ?? EmbeddingGeneratorFingerprintUnknownComponent,
            metadata?.DefaultModelDimensions?.ToString(CultureInfo.InvariantCulture) ?? EmbeddingGeneratorFingerprintUnknownComponent,
            generatorTypeName ?? EmbeddingGeneratorFingerprintUnknownComponent));
    }
}
