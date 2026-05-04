using System.Diagnostics;
using System.Globalization;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayIndexBuildResult> BuildIndexAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existingBuild = Volatile.Read(ref _buildOperation);
        while (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposed();

            if (existingBuild is null)
            {
                var buildSource = new TaskCompletionSource<McpGatewayIndexBuildResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var createdBuild = new BuildOperation(buildSource.Task, cancellationToken);
                if (Interlocked.CompareExchange(ref _buildOperation, createdBuild, null) is null)
                {
                    _ = RunBuildIndexAsync(buildSource, createdBuild);
                    existingBuild = createdBuild;
                    break;
                }

                existingBuild = Volatile.Read(ref _buildOperation);
                continue;
            }

            if (existingBuild.CancellationToken.IsCancellationRequested)
            {
                await AwaitCanceledBuildAsync(existingBuild);
                _ = Interlocked.CompareExchange(ref _buildOperation, null, existingBuild);
                existingBuild = Volatile.Read(ref _buildOperation);
                continue;
            }

            if (existingBuild.Task.IsCanceled || existingBuild.Task.IsFaulted)
            {
                _ = Interlocked.CompareExchange(ref _buildOperation, null, existingBuild);
                existingBuild = Volatile.Read(ref _buildOperation);
                continue;
            }

            break;
        }

        if (existingBuild is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await existingBuild!.Task.WaitAsync(cancellationToken);
    }

    private async Task RunBuildIndexAsync(
        TaskCompletionSource<McpGatewayIndexBuildResult> buildSource,
        BuildOperation buildOperation
    )
    {
        try
        {
            buildSource.SetResult(await BuildIndexCoreAsync(buildOperation.CancellationToken));
        }
        catch (OperationCanceledException)
            when (buildOperation.CancellationToken.IsCancellationRequested)
        {
            buildSource.SetCanceled(buildOperation.CancellationToken);
        }
        catch (Exception ex)
        {
            buildSource.SetException(ex);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _buildOperation, null, buildOperation);
        }
    }

    private async Task<McpGatewayIndexBuildResult> BuildIndexCoreAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var activity = McpGatewayTelemetry.StartBuildIndexActivity(_searchStrategy);
        var stopwatch = Stopwatch.StartNew();
        var registrySnapshot = _catalogSource.CreateSnapshot();
        var diagnostics = new List<McpGatewayDiagnostic>();
        var entries = await BuildCatalogEntriesAsync(
            registrySnapshot,
            diagnostics,
            cancellationToken
        );
        var vectorization = await VectorizeEntriesAsync(entries, diagnostics, cancellationToken);
        var graphIndex = await TryBuildGraphIndexAsync(entries, diagnostics, cancellationToken);
        var snapshot = CreateSnapshot(
            registrySnapshot,
            entries,
            vectorization.IsVectorSearchEnabled,
            graphIndex
        );

        TryUpdateState(snapshot, registrySnapshot.Version);

        _logger.LogInformation(
            GatewayIndexRebuiltLogMessage,
            snapshot.Entries.Count,
            vectorization.VectorizedToolCount,
            graphIndex?.NodeCount ?? 0,
            graphIndex?.EdgeCount ?? 0
        );

        var buildResult = CreateBuildResult(snapshot, diagnostics, vectorization.VectorizedToolCount);
        McpGatewayTelemetry.RecordIndexBuild(
            activity,
            _searchStrategy,
            buildResult,
            vectorization.VectorTokenUsage,
            stopwatch.Elapsed.TotalMilliseconds
        );
        return buildResult;
    }

    private async Task<List<ToolCatalogEntry>> BuildCatalogEntriesAsync(
        McpGatewayCatalogSourceSnapshot registrySnapshot,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        var entries = new List<ToolCatalogEntry>();
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrySnapshot.Registrations)
        {
            var tools = await LoadRegistrationToolsAsync(registration, diagnostics, cancellationToken);
            AddCatalogEntries(entries, seenToolIds, registration, tools, diagnostics);
        }

        return entries;
    }

    private async Task<IReadOnlyList<McpGatewayLoadedTool>> LoadRegistrationToolsAsync(
        McpGatewayToolSourceRegistration registration,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await registration.LoadToolsAsync(_loggerFactory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    SourceLoadFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        SourceLoadFailedMessageFormat,
                        registration.SourceId,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, FailedToLoadGatewaySourceLogMessage, registration.SourceId);
            return [];
        }
    }

    private void AddCatalogEntries(
        ICollection<ToolCatalogEntry> entries,
        ISet<string> seenToolIds,
        McpGatewayToolSourceRegistration registration,
        IReadOnlyList<McpGatewayLoadedTool> tools,
        IList<McpGatewayDiagnostic> diagnostics
    )
    {
        foreach (var loadedTool in tools)
        {
            var descriptor = BuildDescriptor(registration, loadedTool);
            if (descriptor is null)
            {
                continue;
            }

            if (!seenToolIds.Add(descriptor.ToolId))
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        DuplicateToolIdDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            DuplicateToolIdMessageFormat,
                            descriptor.ToolId
                        )
                    )
                );
                continue;
            }

            var document = BuildDescriptorDocument(descriptor);
            var searchTermIndex = BuildToolSearchTermIndex(document);
            entries.Add(
                new ToolCatalogEntry(
                    descriptor,
                    loadedTool.Tool,
                    document,
                    searchTermIndex.SearchBoostTerms,
                    searchTermIndex.ConfidenceTerms
                )
            );
        }
    }

    private async Task<VectorizationOutcome> VectorizeEntriesAsync(
        IList<ToolCatalogEntry> entries,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (
            entries.Count == 0
            || _searchStrategy
                is not (
                    McpGatewaySearchStrategy.Auto or McpGatewaySearchStrategy.Embeddings
                )
        )
        {
            return VectorizationOutcome.Empty;
        }

        var vectorizedToolCount = 0;
        var vectorTokenUsage = VectorTokenUsage.Zero;

        await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
        await using var embeddingStoreLease = ResolveToolEmbeddingStore();
        var embeddingGenerator = embeddingGeneratorLease.Generator;
        var embeddingGeneratorFingerprint = GetOrCreateEmbeddingGeneratorFingerprint(
            embeddingGenerator
        );
        var embeddingStore = embeddingStoreLease.Store;
        var storeCandidates = CreateToolEmbeddingCandidates(entries, embeddingGeneratorFingerprint);

        vectorizedToolCount += await TryLoadStoredEmbeddingsAsync(
            entries,
            storeCandidates,
            embeddingStore,
            diagnostics,
            cancellationToken
        );

        var missingCandidates = storeCandidates
            .Where(candidate => entries[candidate.Index].Magnitude <= double.Epsilon)
            .ToList();

        if (embeddingGenerator is null && vectorizedToolCount > 0)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    EmbeddingGeneratorMissingDiagnosticCode,
                    EmbeddingGeneratorMissingMessage
                )
            );
        }

        if (missingCandidates.Count > 0)
        {
            var embeddingGeneration = await TryGenerateMissingEmbeddingsAsync(
                entries,
                missingCandidates,
                embeddingGenerator,
                embeddingStore,
                diagnostics,
                cancellationToken
            );
            vectorizedToolCount += embeddingGeneration.VectorizedToolCount;
            vectorTokenUsage = embeddingGeneration.VectorTokenUsage;
        }

        return new VectorizationOutcome(
            vectorizedToolCount,
            vectorizedToolCount > 0 && embeddingGenerator is not null,
            vectorTokenUsage
        );
    }

    private static List<ToolEmbeddingCandidate> CreateToolEmbeddingCandidates(
        IEnumerable<ToolCatalogEntry> entries,
        string? embeddingGeneratorFingerprint
    ) =>
        entries
            .Select(
                (entry, index) =>
                    new ToolEmbeddingCandidate(
                        index,
                        new McpGatewayToolEmbeddingLookup(
                            entry.Descriptor.ToolId,
                            ComputeDocumentHash(entry.Document),
                            embeddingGeneratorFingerprint
                        ),
                        entry.Descriptor.SourceId,
                        entry.Descriptor.ToolName
                    )
            )
            .ToList();

    private async Task<int> TryLoadStoredEmbeddingsAsync(
        IList<ToolCatalogEntry> entries,
        IReadOnlyList<ToolEmbeddingCandidate> storeCandidates,
        IMcpGatewayToolEmbeddingStore? embeddingStore,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (embeddingStore is null)
        {
            return 0;
        }

        try
        {
            var vectorizedToolCount = 0;
            var storedEmbeddings = await embeddingStore.GetAsync(
                storeCandidates.Select(static candidate => candidate.Lookup).ToList(),
                cancellationToken
            );

            foreach (var candidate in storeCandidates)
            {
                var storedEmbedding = storedEmbeddings.LastOrDefault(embedding =>
                    MatchesStoredEmbedding(candidate.Lookup, embedding)
                );
                if (storedEmbedding is not null)
                {
                    ApplyEmbedding(
                        entries,
                        candidate.Index,
                        storedEmbedding.Vector,
                        ref vectorizedToolCount
                    );
                }
            }

            return vectorizedToolCount;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    EmbeddingStoreLoadFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        EmbeddingStoreLoadFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, EmbeddingStoreLoadFailedLogMessage);
            return 0;
        }
    }

    private async Task<EmbeddingGenerationOutcome> TryGenerateMissingEmbeddingsAsync(
        IList<ToolCatalogEntry> entries,
        IReadOnlyList<ToolEmbeddingCandidate> missingCandidates,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator,
        IMcpGatewayToolEmbeddingStore? embeddingStore,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (embeddingGenerator is null)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        EmbeddingGeneratorMissingDiagnosticCode,
                        EmbeddingGeneratorMissingMessage
                    )
                );
                return EmbeddingGenerationOutcome.Empty;
            }

            var embeddingDocuments = missingCandidates
                .Select(candidate => entries[candidate.Index].Document)
                .ToList();
            var generatedEmbeddingsBatch = await embeddingGenerator.GenerateAsync(
                embeddingDocuments,
                cancellationToken: cancellationToken
            );
            var vectorTokenUsage = ExtractVectorTokenUsage(
                generatedEmbeddingsBatch.Usage,
                embeddingDocuments
            );
            var embeddings = generatedEmbeddingsBatch.ToList();
            if (embeddings.Count != missingCandidates.Count)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        EmbeddingCountMismatchDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            EmbeddingCountMismatchMessageFormat,
                            embeddings.Count,
                            missingCandidates.Count
                        )
                    )
                );
                return new EmbeddingGenerationOutcome(0, vectorTokenUsage);
            }

            var generatedToolEmbeddings = ApplyGeneratedEmbeddings(
                entries,
                missingCandidates,
                embeddings
            );
            await TryPersistEmbeddingsAsync(
                generatedToolEmbeddings.GeneratedEmbeddings,
                embeddingStore,
                diagnostics,
                cancellationToken
            );

            return new EmbeddingGenerationOutcome(
                generatedToolEmbeddings.VectorizedToolCount,
                vectorTokenUsage
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    EmbeddingFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        EmbeddingFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, EmbeddingGenerationFailedLogMessage);
            return EmbeddingGenerationOutcome.Empty;
        }
    }

    private static GeneratedEmbeddingsResult ApplyGeneratedEmbeddings(
        IList<ToolCatalogEntry> entries,
        IReadOnlyList<ToolEmbeddingCandidate> missingCandidates,
        IReadOnlyList<Embedding<float>> embeddings
    )
    {
        var vectorizedToolCount = 0;
        var generatedEmbeddings = new List<McpGatewayToolEmbedding>(missingCandidates.Count);
        for (var index = 0; index < missingCandidates.Count; index++)
        {
            var candidate = missingCandidates[index];
            var vector = embeddings[index].Vector.ToArray();
            if (ApplyEmbedding(entries, candidate.Index, vector, ref vectorizedToolCount))
            {
                generatedEmbeddings.Add(
                    new McpGatewayToolEmbedding(
                        candidate.Lookup.ToolId,
                        candidate.SourceId,
                        candidate.ToolName,
                        candidate.Lookup.DocumentHash,
                        candidate.Lookup.EmbeddingGeneratorFingerprint,
                        vector
                    )
                );
            }
        }

        return new GeneratedEmbeddingsResult(generatedEmbeddings, vectorizedToolCount);
    }

    private async Task TryPersistEmbeddingsAsync(
        IReadOnlyList<McpGatewayToolEmbedding> generatedEmbeddings,
        IMcpGatewayToolEmbeddingStore? embeddingStore,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (generatedEmbeddings.Count == 0 || embeddingStore is null)
        {
            return;
        }

        try
        {
            await embeddingStore.UpsertAsync(generatedEmbeddings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    EmbeddingStoreSaveFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        EmbeddingStoreSaveFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, EmbeddingStoreSaveFailedLogMessage);
        }
    }

    private async Task<ToolGraphSearchIndex?> TryBuildGraphIndexAsync(
        IReadOnlyList<ToolCatalogEntry> entries,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (entries.Count == 0 || !ShouldBuildGraphSearchIndex())
        {
            return null;
        }

        try
        {
            return await BuildToolGraphSearchIndexAsync(entries, diagnostics, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    GraphBuildFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        GraphBuildFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, GatewayGraphBuildFailedLogMessage);
            return null;
        }
    }

    private static ToolCatalogSnapshot CreateSnapshot(
        McpGatewayCatalogSourceSnapshot registrySnapshot,
        IEnumerable<ToolCatalogEntry> entries,
        bool isVectorSearchEnabled,
        ToolGraphSearchIndex? graphIndex
    ) =>
        new(
            entries
                .OrderBy(static item => item.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            isVectorSearchEnabled,
            graphIndex,
            registrySnapshot.Version
        );

    private static McpGatewayIndexBuildResult CreateBuildResult(
        ToolCatalogSnapshot snapshot,
        IReadOnlyList<McpGatewayDiagnostic> diagnostics,
        int vectorizedToolCount
    ) =>
        new(snapshot.Entries.Count, vectorizedToolCount, snapshot.HasVectors, diagnostics)
        {
            IsGraphSearchEnabled = snapshot.GraphIndex?.CanSearch ?? false,
            GraphNodeCount = snapshot.GraphIndex?.NodeCount ?? 0,
            GraphEdgeCount = snapshot.GraphIndex?.EdgeCount ?? 0,
        };

    private void TryUpdateState(ToolCatalogSnapshot snapshot, int snapshotVersion)
    {
        var state = Volatile.Read(ref _state);
        while (!state.IsDisposed)
        {
            var updatedState = state with
            {
                Snapshot = snapshot,
                SnapshotVersion = snapshotVersion,
            };
            if (
                ReferenceEquals(Interlocked.CompareExchange(ref _state, updatedState, state), state)
            )
            {
                return;
            }

            state = Volatile.Read(ref _state);
        }
    }

    private static async Task AwaitCanceledBuildAsync(BuildOperation buildOperation)
    {
        try
        {
            await buildOperation.Task;
        }
        catch (OperationCanceledException)
            when (buildOperation.CancellationToken.IsCancellationRequested)
        { }
    }

    private sealed record BuildOperation(
        Task<McpGatewayIndexBuildResult> Task,
        CancellationToken CancellationToken
    );

    private sealed record VectorizationOutcome(
        int VectorizedToolCount,
        bool IsVectorSearchEnabled,
        VectorTokenUsage VectorTokenUsage
    )
    {
        public static VectorizationOutcome Empty { get; } = new(0, false, VectorTokenUsage.Zero);
    }

    private sealed record EmbeddingGenerationOutcome(
        int VectorizedToolCount,
        VectorTokenUsage VectorTokenUsage
    )
    {
        public static EmbeddingGenerationOutcome Empty { get; } = new(0, VectorTokenUsage.Zero);
    }

    private sealed record GeneratedEmbeddingsResult(
        IReadOnlyList<McpGatewayToolEmbedding> GeneratedEmbeddings,
        int VectorizedToolCount
    );
}
