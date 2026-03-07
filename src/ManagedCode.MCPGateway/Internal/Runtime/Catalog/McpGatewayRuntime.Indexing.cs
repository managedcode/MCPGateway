using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayIndexBuildResult> BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var existingBuild = Volatile.Read(ref _buildTask);
        while (existingBuild is null && !cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposed();

            var buildSource = new TaskCompletionSource<McpGatewayIndexBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _buildTask, buildSource.Task, null) is null)
            {
                _ = RunBuildIndexAsync(buildSource);
                existingBuild = buildSource.Task;
                break;
            }

            existingBuild = Volatile.Read(ref _buildTask);
        }

        if (existingBuild is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await existingBuild!.WaitAsync(cancellationToken);
    }

    private async Task RunBuildIndexAsync(TaskCompletionSource<McpGatewayIndexBuildResult> buildSource)
    {
        try
        {
            buildSource.SetResult(await BuildIndexCoreAsync());
        }
        catch (Exception ex)
        {
            buildSource.SetException(ex);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _buildTask, null, buildSource.Task);
        }
    }

    private async Task<McpGatewayIndexBuildResult> BuildIndexCoreAsync()
    {
        ThrowIfDisposed();

        var registrySnapshot = _catalogSource.CreateSnapshot();
        var diagnostics = new List<McpGatewayDiagnostic>();
        var entries = new List<ToolCatalogEntry>();
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registration in registrySnapshot.Registrations)
        {
            IReadOnlyList<AITool> tools;
            try
            {
                tools = await registration.LoadToolsAsync(_loggerFactory, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    SourceLoadFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        SourceLoadFailedMessageFormat,
                        registration.SourceId,
                        ex.GetBaseException().Message)));
                _logger.LogWarning(ex, FailedToLoadGatewaySourceLogMessage, registration.SourceId);
                continue;
            }

            foreach (var tool in tools)
            {
                var descriptor = BuildDescriptor(registration, tool);
                if (descriptor is null)
                {
                    continue;
                }

                if (!seenToolIds.Add(descriptor.ToolId))
                {
                    diagnostics.Add(new McpGatewayDiagnostic(
                        DuplicateToolIdDiagnosticCode,
                        string.Format(CultureInfo.InvariantCulture, DuplicateToolIdMessageFormat, descriptor.ToolId)));
                    continue;
                }

                var tokenSearchSegments = BuildDescriptorTokenSearchSegments(descriptor);
                var searchFields = BuildTokenizedSearchFields(tokenSearchSegments);
                entries.Add(new ToolCatalogEntry(
                    descriptor,
                    tool,
                    BuildDescriptorDocument(descriptor, tool),
                    searchFields,
                    BuildLexicalTerms(tokenSearchSegments),
                    TokenSearchProfile.Empty,
                    TokenSearchProfile.Empty));
            }
        }

        var rawTokenProfiles = entries
            .Select(entry => BuildTokenSearchProfile(entry.SearchFields))
            .ToList();
        var rawCharacterNGramProfiles = entries
            .Select(entry => BuildCharacterNGramProfile(entry.SearchFields))
            .ToList();
        var tokenInverseDocumentFrequencies = BuildTokenInverseDocumentFrequencies(rawTokenProfiles);
        var characterNGramInverseDocumentFrequencies = BuildTokenInverseDocumentFrequencies(rawCharacterNGramProfiles);
        var averageSearchFieldLength = CalculateAverageSearchFieldLength(entries);
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index] = entries[index] with
            {
                TokenProfile = ApplyTokenInverseDocumentFrequencies(rawTokenProfiles[index], tokenInverseDocumentFrequencies),
                CharacterNGramProfile = ApplyTokenInverseDocumentFrequencies(
                    rawCharacterNGramProfiles[index],
                    characterNGramInverseDocumentFrequencies)
            };
        }

        var vectorizedToolCount = 0;
        var isVectorSearchEnabled = false;
        if (entries.Count > 0 && _searchStrategy is not McpGatewaySearchStrategy.Tokenizer)
        {
            await using var embeddingGeneratorLease = ResolveEmbeddingGenerator();
            await using var embeddingStoreLease = ResolveToolEmbeddingStore();
            var embeddingGenerator = embeddingGeneratorLease.Generator;
            var embeddingGeneratorFingerprint = ResolveEmbeddingGeneratorFingerprint(embeddingGenerator);
            var embeddingStore = embeddingStoreLease.Store;
            var storeCandidates = entries
                .Select((entry, index) => new ToolEmbeddingCandidate(
                    index,
                    new McpGatewayToolEmbeddingLookup(
                        entry.Descriptor.ToolId,
                        ComputeDocumentHash(entry.Document),
                        embeddingGeneratorFingerprint),
                    entry.Descriptor.SourceId,
                    entry.Descriptor.ToolName))
                .ToList();

            if (embeddingStore is not null)
            {
                try
                {
                    var storedEmbeddings = await embeddingStore.GetAsync(
                        storeCandidates.Select(static candidate => candidate.Lookup).ToList(),
                        CancellationToken.None);

                    foreach (var candidate in storeCandidates)
                    {
                        var storedEmbedding = storedEmbeddings.LastOrDefault(embedding =>
                            MatchesStoredEmbedding(candidate.Lookup, embedding));
                        if (storedEmbedding is not null)
                        {
                            ApplyEmbedding(entries, candidate.Index, storedEmbedding.Vector, ref vectorizedToolCount);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(
                        EmbeddingStoreLoadFailedDiagnosticCode,
                        string.Format(CultureInfo.InvariantCulture, EmbeddingStoreLoadFailedMessageFormat, ex.GetBaseException().Message)));
                    _logger.LogWarning(ex, EmbeddingStoreLoadFailedLogMessage);
                }
            }

            var missingCandidates = storeCandidates
                .Where(candidate => entries[candidate.Index].Magnitude <= double.Epsilon)
                .ToList();

            if (embeddingGenerator is null && vectorizedToolCount > 0)
            {
                diagnostics.Add(new McpGatewayDiagnostic(
                    EmbeddingGeneratorMissingDiagnosticCode,
                    EmbeddingGeneratorMissingMessage));
            }

            if (missingCandidates.Count > 0)
            {
                try
                {
                    if (embeddingGenerator is not null)
                    {
                        var embeddings = (await embeddingGenerator.GenerateAsync(
                                missingCandidates.Select(candidate => entries[candidate.Index].Document),
                                cancellationToken: CancellationToken.None))
                            .ToList();
                        if (embeddings.Count == missingCandidates.Count)
                        {
                            var generatedEmbeddings = new List<McpGatewayToolEmbedding>(missingCandidates.Count);
                            for (var index = 0; index < missingCandidates.Count; index++)
                            {
                                var candidate = missingCandidates[index];
                                var vector = embeddings[index].Vector.ToArray();
                                if (ApplyEmbedding(entries, candidate.Index, vector, ref vectorizedToolCount))
                                {
                                    generatedEmbeddings.Add(new McpGatewayToolEmbedding(
                                        candidate.Lookup.ToolId,
                                        candidate.SourceId,
                                        candidate.ToolName,
                                        candidate.Lookup.DocumentHash,
                                        candidate.Lookup.EmbeddingGeneratorFingerprint,
                                        vector));
                                }
                            }

                            if (generatedEmbeddings.Count > 0 && embeddingStore is not null)
                            {
                                try
                                {
                                    await embeddingStore.UpsertAsync(generatedEmbeddings, CancellationToken.None);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    diagnostics.Add(new McpGatewayDiagnostic(
                                        EmbeddingStoreSaveFailedDiagnosticCode,
                                        string.Format(CultureInfo.InvariantCulture, EmbeddingStoreSaveFailedMessageFormat, ex.GetBaseException().Message)));
                                    _logger.LogWarning(ex, EmbeddingStoreSaveFailedLogMessage);
                                }
                            }
                        }
                        else
                        {
                            diagnostics.Add(new McpGatewayDiagnostic(
                                EmbeddingCountMismatchDiagnosticCode,
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    EmbeddingCountMismatchMessageFormat,
                                    embeddings.Count,
                                    missingCandidates.Count)));
                        }
                    }
                    else
                    {
                        diagnostics.Add(new McpGatewayDiagnostic(
                            EmbeddingGeneratorMissingDiagnosticCode,
                            EmbeddingGeneratorMissingMessage));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add(new McpGatewayDiagnostic(
                        EmbeddingFailedDiagnosticCode,
                        string.Format(CultureInfo.InvariantCulture, EmbeddingFailedMessageFormat, ex.GetBaseException().Message)));
                    _logger.LogWarning(ex, EmbeddingGenerationFailedLogMessage);
                }
            }

            isVectorSearchEnabled = vectorizedToolCount > 0 && embeddingGenerator is not null;
        }

        var snapshot = new ToolCatalogSnapshot(
            entries
                .OrderBy(static item => item.Descriptor.ToolName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            isVectorSearchEnabled,
            tokenInverseDocumentFrequencies,
            characterNGramInverseDocumentFrequencies,
            averageSearchFieldLength);

        TryUpdateState(snapshot, registrySnapshot.Version);

        _logger.LogInformation(
            GatewayIndexRebuiltLogMessage,
            snapshot.Entries.Count,
            vectorizedToolCount);

        return new McpGatewayIndexBuildResult(
            snapshot.Entries.Count,
            vectorizedToolCount,
            snapshot.HasVectors,
            diagnostics);
    }

    private void TryUpdateState(ToolCatalogSnapshot snapshot, int snapshotVersion)
    {
        var state = Volatile.Read(ref _state);
        while (!state.IsDisposed)
        {
            var updatedState = state with
            {
                Snapshot = snapshot,
                SnapshotVersion = snapshotVersion
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, updatedState, state), state))
            {
                return;
            }

            state = Volatile.Read(ref _state);
        }
    }

    private static double CalculateAverageSearchFieldLength(IReadOnlyList<ToolCatalogEntry> entries)
    {
        var totalLength = 0d;
        var fieldCount = 0;
        foreach (var entry in entries)
        {
            foreach (var field in entry.SearchFields)
            {
                totalLength += Math.Max(1, field.Length);
                fieldCount++;
            }
        }

        return fieldCount == 0
            ? 1d
            : totalLength / fieldCount;
    }
}
