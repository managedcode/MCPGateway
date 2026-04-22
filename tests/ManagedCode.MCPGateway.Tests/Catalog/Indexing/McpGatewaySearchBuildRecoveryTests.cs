using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewaySearchTests
{
    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingStoreLoadFailureAndStillGeneratesVectors()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        var embeddingStore = new FaultingToolEmbeddingStore(throwOnGet: true);

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureVectorSearchTools,
            embeddingGenerator,
            embeddingStore
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingStore.GetAttempts).IsEqualTo(1);
        await Assert.That(embeddingStore.UpsertAttempts).IsEqualTo(1);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "embedding_store_load_failed"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_ReportsEmbeddingStoreSaveFailureAfterGeneratingVectors()
    {
        var embeddingGenerator = new TestEmbeddingGenerator();
        var embeddingStore = new FaultingToolEmbeddingStore(throwOnUpsert: true);

        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(
            ConfigureVectorSearchTools,
            embeddingGenerator,
            embeddingStore
        );
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        var buildResult = await gateway.BuildIndexAsync();

        await Assert.That(buildResult.VectorizedToolCount).IsEqualTo(2);
        await Assert.That(buildResult.IsVectorSearchEnabled).IsTrue();
        await Assert.That(embeddingGenerator.Calls.Count).IsEqualTo(1);
        await Assert.That(embeddingStore.GetAttempts).IsEqualTo(1);
        await Assert.That(embeddingStore.UpsertAttempts).IsEqualTo(1);
        await Assert
            .That(
                buildResult.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "embedding_store_save_failed"
                )
            )
            .IsTrue();
    }

    [TUnit.Core.Test]
    public async Task BuildIndexAsync_AllowsRetryAfterFaultedCatalogSnapshotBuild()
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddMcpGateway();

        var faultingSource = new FaultingCatalogSource();
        services.AddSingleton<IMcpGatewayCatalogSource>(faultingSource);

        await using var serviceProvider = services.BuildServiceProvider();
        var gateway = serviceProvider.GetRequiredService<IMcpGateway>();

        InvalidOperationException? buildFailure = null;
        try
        {
            await gateway.BuildIndexAsync();
        }
        catch (InvalidOperationException ex)
        {
            buildFailure = ex;
        }

        var secondBuild = await gateway.BuildIndexAsync();

        await Assert.That(buildFailure).IsNotNull();
        await Assert.That(buildFailure!.Message).IsEqualTo("snapshot creation failed");
        await Assert.That(faultingSource.Attempts).IsEqualTo(2);
        await Assert.That(secondBuild.ToolCount).IsEqualTo(0);
        await Assert.That(secondBuild.Diagnostics.Count).IsEqualTo(0);
    }

    private sealed class FaultingToolEmbeddingStore(bool throwOnGet = false, bool throwOnUpsert = false)
        : IMcpGatewayToolEmbeddingStore
    {
        public int GetAttempts { get; private set; }

        public int UpsertAttempts { get; private set; }

        public Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
            IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetAttempts++;

            if (throwOnGet)
            {
                throw new InvalidOperationException("embedding store load failed");
            }

            return Task.FromResult<IReadOnlyList<McpGatewayToolEmbedding>>([]);
        }

        public Task UpsertAsync(
            IReadOnlyList<McpGatewayToolEmbedding> embeddings,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertAttempts++;

            if (throwOnUpsert)
            {
                throw new InvalidOperationException("embedding store save failed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FaultingCatalogSource : IMcpGatewayCatalogSource
    {
        private int _attempts;

        public int Attempts => _attempts;

        public McpGatewayCatalogSourceSnapshot CreateSnapshot()
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                throw new InvalidOperationException("snapshot creation failed");
            }

            return new McpGatewayCatalogSourceSnapshot(0, []);
        }
    }
}
