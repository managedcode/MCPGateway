using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<(List<ToolCatalogEntry> Entries, IReadOnlyDictionary<McpGatewayToolSourceRegistration, IReadOnlyList<McpGatewayLoadedTool>> LoadedTools)> BuildCatalogEntriesAsync(
        McpGatewayCatalogSourceSnapshot registrySnapshot,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        var entries = new List<ToolCatalogEntry>();
        var loaded = new Dictionary<McpGatewayToolSourceRegistration, IReadOnlyList<McpGatewayLoadedTool>>();
        var previous = Volatile.Read(ref _state).Snapshot;
        var approvedNames = _managedCatalog
            ? (await McpGatewayTrustedContext.OperationServices(_serviceProvider).GetRequiredService<IMcpGatewayCatalogDefinitionStore>()
                .ReadAsync(cancellationToken)).Definitions.ToDictionary(definition =>
                    (definition.SourceId.ToLowerInvariant(), definition.UpstreamToolName), definition => definition.ToolId)
            : new Dictionary<(string, string?), string>();
        var reservedToolIds = approvedNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedRegistrations =
            new List<(McpGatewayToolSourceRegistration Registration, IReadOnlyList<McpGatewayLoadedTool> Tools)>();

        foreach (var registration in registrySnapshot.Registrations)
        {
            var tools = registrySnapshot.RefreshSourceId is not null
                && !registration.SourceId.Equals(registrySnapshot.RefreshSourceId, StringComparison.OrdinalIgnoreCase)
                && previous.LoadedTools.TryGetValue(registration, out var cached)
                    ? cached : await LoadRegistrationToolsAsync(registration, diagnostics, cancellationToken);
            loaded.Add(registration, tools);
            loadedRegistrations.Add((registration, tools));
        }

        var normalizedToolNameCounts = CountNormalizedToolNames(loadedRegistrations);

        foreach (var (registration, tools) in loadedRegistrations)
        {
            AddCatalogEntries(
                entries,
                reservedToolIds,
                normalizedToolNameCounts,
                approvedNames,
                registration,
                tools,
                diagnostics
            );
        }

        return (entries, loaded);
    }

    private async Task<IReadOnlyList<McpGatewayLoadedTool>> LoadRegistrationToolsAsync(
        McpGatewayToolSourceRegistration registration,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (_managedCatalog && registration is McpGatewayHttpToolSourceRegistration { HasControlledHttpClient: false })
        { throw new InvalidOperationException("Managed HTTP catalog discovery requires a source-bound McpGatewayEgressHttpClient."); }
        try
        {
            return await registration.LoadToolsAsync(_loggerFactory, cancellationToken);
        }
        catch (Exception ex) when (McpGatewayRuntimeFailureClassifier.IsRecoverable(ex))
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    SourceLoadFailedDiagnosticCode,
                    SourceLoadFailedMessage
                )
            );
            _logger.LogWarning(FailedToLoadGatewaySourceLogMessage);
            return [];
        }
    }

    private void AddCatalogEntries(
        ICollection<ToolCatalogEntry> entries,
        ISet<string> reservedToolIds,
        IReadOnlyDictionary<string, int> normalizedToolNameCounts,
        IReadOnlyDictionary<(string, string?), string> approvedNames,
        McpGatewayToolSourceRegistration registration,
        IReadOnlyList<McpGatewayLoadedTool> tools,
        IList<McpGatewayDiagnostic> diagnostics
    )
    {
        foreach (var loadedTool in tools)
        {
            if (string.IsNullOrWhiteSpace(loadedTool.Tool.Name))
            {
                continue;
            }

            var toolName = loadedTool.Tool.Name.Trim();
            var normalizedToolName = McpGatewayProtocolName.Normalize(
                toolName,
                McpGatewayProtocolName.DefaultToolName
            );
            var hasNameCollision = normalizedToolNameCounts.TryGetValue(normalizedToolName, out var count) && count > 1;
            var requiresSourcePrefix = _managedCatalog || hasNameCollision;
            var toolId = approvedNames.TryGetValue((registration.SourceId.ToLowerInvariant(), loadedTool.Tool.Name), out var approvedName)
                ? approvedName : McpGatewayProtocolName.CreateToolId(
                registration.SourceId,
                toolName,
                reservedToolIds,
                requiresSourcePrefix
            );
            var descriptor = BuildDescriptor(registration, loadedTool, toolName, toolId);
            if (_managedCatalog && loadedTool.Client is not null && !_executionOptions.RemoteBindings.ContainsKey(registration.SourceId))
            { throw new InvalidOperationException("Managed remote sources require an explicit reviewed credential binding before publication."); }
            if (_executionOptions.RemoteBindings.TryGetValue(registration.SourceId, out var binding))
            {
                descriptor = descriptor with
                {
                    BindingFingerprint = McpGatewayDefinitionHash.Compute(new
                    { descriptor.BindingFingerprint, binding.BindingReference, binding.Audience, binding.RequiredScopes, binding.UseSharedTechnicalClient })
                };
            }

            if (hasNameCollision)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        DuplicateToolIdDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            DuplicateToolIdMessageFormat,
                            normalizedToolName
                        )
                    )
                );
            }

            var document = BuildDescriptorDocument(descriptor);
            var searchTermIndex = BuildToolSearchTermIndex(document);
            entries.Add(
                new ToolCatalogEntry(
                    descriptor,
                    loadedTool.Tool,
                    document,
                    searchTermIndex.SearchBoostTerms,
                    searchTermIndex.ConfidenceTerms,
                    loadedTool.Client
                )
            );
        }
    }

    private static IReadOnlyDictionary<string, int> CountNormalizedToolNames(
        IReadOnlyList<(McpGatewayToolSourceRegistration Registration, IReadOnlyList<McpGatewayLoadedTool> Tools)> loadedRegistrations
    )
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, tools) in loadedRegistrations)
        {
            foreach (var loadedTool in tools)
            {
                if (string.IsNullOrWhiteSpace(loadedTool.Tool.Name))
                {
                    continue;
                }

                var normalizedToolName = McpGatewayProtocolName.Normalize(
                    loadedTool.Tool.Name,
                    McpGatewayProtocolName.DefaultToolName
                );
                counts[normalizedToolName] = counts.GetValueOrDefault(normalizedToolName) + 1;
            }
        }

        return counts;
    }

}
