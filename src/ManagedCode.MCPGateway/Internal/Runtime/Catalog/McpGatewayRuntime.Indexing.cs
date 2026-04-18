using System.Diagnostics;
using System.Globalization;
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
        var entries = new List<ToolCatalogEntry>();
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrySnapshot.Registrations)
        {
            IReadOnlyList<McpGatewayLoadedTool> tools;
            try
            {
                tools = await registration.LoadToolsAsync(_loggerFactory, cancellationToken);
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
                continue;
            }

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

                entries.Add(
                    new ToolCatalogEntry(
                        descriptor,
                        loadedTool.Tool,
                        BuildDescriptorDocument(descriptor)
                    )
                );
            }
        }

        var vectorizedToolCount = 0;
        var isVectorSearchEnabled = false;
        var vectorTokenUsage = VectorTokenUsage.Zero;
        if (
            entries.Count > 0
            && _searchStrategy
                is McpGatewaySearchStrategy.Auto
                    or McpGatewaySearchStrategy.Embeddings
        )
        {
            await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
            await using var embeddingStoreLease = ResolveToolEmbeddingStore();
            var embeddingGenerator = embeddingGeneratorLease.Generator;
            var embeddingGeneratorFingerprint = GetOrCreateEmbeddingGeneratorFingerprint(
                embeddingGenerator
            );
            var embeddingStore = embeddingStoreLease.Store;
            var storeCandidates = entries
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

            if (embeddingStore is not null)
            {
                try
                {
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
                }
            }

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
                try
                {
                    if (embeddingGenerator is not null)
                    {
                        var embeddingDocuments = missingCandidates
                            .Select(candidate => entries[candidate.Index].Document)
                            .ToList();
                        var generatedEmbeddingsBatch = await embeddingGenerator.GenerateAsync(
                            embeddingDocuments,
                            cancellationToken: cancellationToken
                        );
                        vectorTokenUsage = ExtractVectorTokenUsage(
                            generatedEmbeddingsBatch.Usage,
                            embeddingDocuments
                        );
                        var embeddings = generatedEmbeddingsBatch.ToList();
                        if (embeddings.Count == missingCandidates.Count)
                        {
                            var generatedEmbeddings = new List<McpGatewayToolEmbedding>(
                                missingCandidates.Count
                            );
                            for (var index = 0; index < missingCandidates.Count; index++)
                            {
                                var candidate = missingCandidates[index];
                                var vector = embeddings[index].Vector.ToArray();
                                if (
                                    ApplyEmbedding(
                                        entries,
                                        candidate.Index,
                                        vector,
                                        ref vectorizedToolCount
                                    )
                                )
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

                            if (generatedEmbeddings.Count > 0 && embeddingStore is not null)
                            {
                                try
                                {
                                    await embeddingStore.UpsertAsync(
                                        generatedEmbeddings,
                                        cancellationToken
                                    );
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
                        }
                        else
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
                        }
                    }
                    else
                    {
                        diagnostics.Add(
                            new McpGatewayDiagnostic(
                                EmbeddingGeneratorMissingDiagnosticCode,
                                EmbeddingGeneratorMissingMessage
                            )
                        );
                    }
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
                }
            }

            isVectorSearchEnabled = vectorizedToolCount > 0 && embeddingGenerator is not null;
        }

        ToolGraphSearchIndex? graphIndex = null;
        if (entries.Count > 0 && ShouldBuildGraphSearchIndex())
        {
            try
            {
                graphIndex = await BuildToolGraphSearchIndexAsync(
                    entries,
                    diagnostics,
                    cancellationToken
                );
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
            }
        }

        var snapshot = new ToolCatalogSnapshot(
            entries
                .OrderBy(static item => item.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            isVectorSearchEnabled,
            graphIndex,
            registrySnapshot.Version
        );

        TryUpdateState(snapshot, registrySnapshot.Version);

        _logger.LogInformation(
            GatewayIndexRebuiltLogMessage,
            snapshot.Entries.Count,
            vectorizedToolCount,
            graphIndex?.NodeCount ?? 0,
            graphIndex?.EdgeCount ?? 0
        );

        var buildResult = new McpGatewayIndexBuildResult(
            snapshot.Entries.Count,
            vectorizedToolCount,
            snapshot.HasVectors,
            diagnostics
        )
        {
            IsGraphSearchEnabled = graphIndex?.CanSearch ?? false,
            GraphNodeCount = graphIndex?.NodeCount ?? 0,
            GraphEdgeCount = graphIndex?.EdgeCount ?? 0,
        };
        McpGatewayTelemetry.RecordIndexBuild(
            activity,
            _searchStrategy,
            buildResult,
            vectorTokenUsage,
            stopwatch.Elapsed.TotalMilliseconds
        );
        return buildResult;
    }

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
}
