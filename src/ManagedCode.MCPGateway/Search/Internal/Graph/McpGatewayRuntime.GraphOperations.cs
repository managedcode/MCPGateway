using System.Globalization;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayGraphSearchResult> SearchGraphAsync(
        McpGatewayGraphSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<McpGatewayDiagnostic>();
        if (snapshot.GraphIndex is not ToolGraphSearchIndex graphIndex)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(GraphUnavailableDiagnosticCode, GraphUnavailableMessage)
            );
            return new McpGatewayGraphSearchResult([], [], [], diagnostics, request.UseFederation);
        }

        var limit = Math.Clamp(
            request.MaxResults.GetValueOrDefault(_defaultSearchLimit),
            1,
            _maxSearchResults
        );
        var schemaQuery = NormalizeGraphSchemaQuery(request.Query);
        var serviceEndpoints = ResolveGraphFederatedServiceEndpoints(request, diagnostics);
        var schemaServiceEndpoints = request.UseFederation ? serviceEndpoints : [];
        var profile = CreateToolGraphSchemaSearchProfile(
            limit,
            GraphFocusedRelatedResultsLimit,
            GraphFocusedNextStepResultsLimit,
            schemaServiceEndpoints
        );
        AddSchemaProfileDiagnostics(graphIndex.SchemaDiagnostics, diagnostics);

        try
        {
            if (request.UseFederation)
            {
                var result = await graphIndex
                    .Graph.SearchBySchemaFederatedAsync(
                        schemaQuery,
                        profile,
                        CreateFederatedSparqlExecutionOptions(
                            graphIndex,
                            serviceEndpoints,
                            request.IncludeLocalGatewayGraph
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                var scoreContext = CreateSearchScoreContext(schemaQuery);
                return MapGraphSearchResult(
                    graphIndex,
                    result,
                    diagnostics,
                    scoreContext,
                    isFederated: true
                );
            }

            var schemaSearch = await SearchGraphBySchemaAsync(
                    graphIndex,
                    schemaQuery,
                    profile,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return MapGraphSearchResult(
                graphIndex,
                schemaSearch,
                diagnostics,
                CreateSearchScoreContext(schemaQuery)
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    GraphSearchFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        GraphSearchFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, GatewayGraphSearchFailedLogMessage);
            return new McpGatewayGraphSearchResult([], [], [], diagnostics, request.UseFederation);
        }
    }

    public async Task<McpGatewayMarkdownLdGraphExport> ExportMarkdownLdGraphAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.GraphIndex is not ToolGraphSearchIndex graphIndex)
        {
            return new McpGatewayMarkdownLdGraphExport(
                JsonLd: string.Empty,
                Turtle: string.Empty,
                MermaidFlowchart: string.Empty,
                DotGraph: string.Empty,
                NodeCount: 0,
                EdgeCount: 0
            );
        }

        return new McpGatewayMarkdownLdGraphExport(
            graphIndex.Graph.SerializeJsonLd(),
            graphIndex.Graph.SerializeTurtle(),
            graphIndex.Graph.SerializeMermaidFlowchart(),
            graphIndex.Graph.SerializeDotGraph(),
            graphIndex.NodeCount,
            graphIndex.EdgeCount
        );
    }

    private IReadOnlyList<Uri> ResolveGraphFederatedServiceEndpoints(
        McpGatewayGraphSearchRequest request,
        IList<McpGatewayDiagnostic> diagnostics
    )
    {
        if (!request.UseFederation)
        {
            return [];
        }

        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (request.IncludeLocalGatewayGraph)
        {
            endpoints[_graphLocalFederationEndpoint.AbsoluteUri] = _graphLocalFederationEndpoint;
        }

        if (request.ServiceEndpoints.Count == 0)
        {
            foreach (var configuredEndpoint in _markdownLdFederatedServiceEndpoints)
            {
                endpoints[configuredEndpoint.AbsoluteUri] = configuredEndpoint;
            }

            return endpoints.Values.ToArray();
        }

        var allowedEndpointUris = _markdownLdFederatedServiceEndpoints
            .Select(static endpoint => endpoint.AbsoluteUri)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var endpointText in request.ServiceEndpoints)
        {
            if (
                !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
                || string.IsNullOrWhiteSpace(endpoint.AbsoluteUri)
            )
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        GraphFederationEndpointInvalidDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            GraphFederationEndpointInvalidMessageFormat,
                            endpointText
                        )
                    )
                );
                continue;
            }

            if (!allowedEndpointUris.Contains(endpoint.AbsoluteUri))
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(
                        GraphFederationEndpointBlockedDiagnosticCode,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            GraphFederationEndpointBlockedMessageFormat,
                            endpoint.AbsoluteUri
                        )
                    )
                );
                continue;
            }

            endpoints[endpoint.AbsoluteUri] = endpoint;
        }

        return endpoints.Values.ToArray();
    }

    private FederatedSparqlExecutionOptions CreateFederatedSparqlExecutionOptions(
        ToolGraphSearchIndex graphIndex,
        IReadOnlyList<Uri> serviceEndpoints,
        bool includeLocalGatewayGraph
    )
    {
        IReadOnlyList<FederatedSparqlLocalServiceBinding> localBindings =
            includeLocalGatewayGraph
            && serviceEndpoints.Any(endpoint =>
                string.Equals(
                    endpoint.AbsoluteUri,
                    _graphLocalFederationEndpoint.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                ? [
                    new FederatedSparqlLocalServiceBinding(
                        _graphLocalFederationEndpoint,
                        graphIndex.Graph
                    ),
                ]
                : [];

        return new FederatedSparqlExecutionOptions
        {
            AllowedServiceEndpoints = serviceEndpoints,
            LocalServiceBindings = localBindings,
            QueryExecutionTimeoutMilliseconds = _markdownLdFederatedQueryTimeoutMilliseconds,
        };
    }

    private static McpGatewayGraphSearchResult MapGraphSearchResult(
        ToolGraphSearchIndex graphIndex,
        SchemaGraphSearch schemaSearch,
        IReadOnlyList<McpGatewayDiagnostic> diagnostics,
        SearchScoreContext scoreContext
    )
    {
        if (!schemaSearch.UsedCandidateGraph)
        {
            return MapGraphSearchResult(
                graphIndex,
                schemaSearch.Result,
                diagnostics,
                scoreContext,
                isFederated: false
            );
        }

        var focusedSearch = CreateFocusedGraphSearchFromSchema(graphIndex, schemaSearch);
        var result = schemaSearch.Result;
        return new McpGatewayGraphSearchResult(
            MapGraphSearchMatches(graphIndex, result.Matches, scoreContext),
            MapFocusedGraphSearchMatches(graphIndex, focusedSearch.RelatedMatches, scoreContext),
            MapFocusedGraphSearchMatches(graphIndex, focusedSearch.NextStepMatches, scoreContext),
            diagnostics,
            IsFederated: false
        )
        {
            GeneratedSparql = result.GeneratedSparql,
            GeneratedExpansionSparql = result.GeneratedExpansionSparql,
            ServiceEndpointSpecifiers = result.ServiceEndpointSpecifiers,
            FocusedGraphNodeCount = focusedSearch.FocusedGraph.Nodes.Count,
            FocusedGraphEdgeCount = focusedSearch.FocusedGraph.Edges.Count,
        };
    }

    private static McpGatewayGraphSearchResult MapGraphSearchResult(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphSchemaSearchResult result,
        IReadOnlyList<McpGatewayDiagnostic> diagnostics,
        SearchScoreContext scoreContext,
        bool isFederated
    ) =>
        new(
            MapGraphSearchMatches(graphIndex, result.Matches, scoreContext),
            MapGraphSearchMatches(graphIndex, result.RelatedMatches, scoreContext),
            MapGraphSearchMatches(graphIndex, result.NextStepMatches, scoreContext),
            diagnostics,
            isFederated
        )
        {
            GeneratedSparql = result.GeneratedSparql,
            GeneratedExpansionSparql = result.GeneratedExpansionSparql,
            ServiceEndpointSpecifiers = result.ServiceEndpointSpecifiers,
            FocusedGraphNodeCount = result.FocusedGraph.Nodes.Count,
            FocusedGraphEdgeCount = result.FocusedGraph.Edges.Count,
        };

    private static IReadOnlyList<McpGatewayGraphSearchMatch> MapGraphSearchMatches(
        ToolGraphSearchIndex graphIndex,
        IEnumerable<KnowledgeGraphSchemaSearchMatch> matches,
        SearchScoreContext scoreContext
    )
    {
        var mappedMatches = new List<McpGatewayGraphSearchMatch>();
        foreach (var match in matches)
        {
            mappedMatches.Add(MapGraphSearchMatch(graphIndex, match, scoreContext));
        }

        return mappedMatches
            .OrderByDescending(static match => match.ToolMatch?.Score ?? match.Score)
            .ThenBy(static match => match.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<McpGatewayGraphSearchMatch> MapFocusedGraphSearchMatches(
        ToolGraphSearchIndex graphIndex,
        IEnumerable<KnowledgeGraphFocusedSearchMatch> matches,
        SearchScoreContext scoreContext
    )
    {
        var mappedMatches = new List<McpGatewayGraphSearchMatch>();
        foreach (var match in matches)
        {
            mappedMatches.Add(MapFocusedGraphSearchMatch(graphIndex, match, scoreContext));
        }

        return mappedMatches
            .OrderByDescending(static match => match.ToolMatch?.Score ?? match.Score)
            .ThenBy(static match => match.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static McpGatewayGraphSearchMatch MapFocusedGraphSearchMatch(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphFocusedSearchMatch match,
        SearchScoreContext scoreContext
    )
    {
        var toolMatch = TryCreateFocusedGraphToolSearchMatch(graphIndex, match, scoreContext);
        return new McpGatewayGraphSearchMatch(
            match.NodeId,
            match.Label,
            match.Role.ToString(),
            Math.Clamp(match.Score, 0d, 1d),
            [],
            []
        )
        {
            SourceNodeId = match.SourceNodeId,
            ToolMatch = toolMatch,
        };
    }

    private static McpGatewayGraphSearchMatch MapGraphSearchMatch(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphSchemaSearchMatch match,
        SearchScoreContext scoreContext
    )
    {
        var toolMatch = TryCreateGraphToolSearchMatch(graphIndex, match, scoreContext);
        return new McpGatewayGraphSearchMatch(
            match.NodeId,
            match.Label,
            match.Role.ToString(),
            Math.Clamp(match.Score, 0d, 1d),
            match.Types,
            MapGraphSearchEvidence(match.Evidence)
        )
        {
            Description = match.Description,
            SourceNodeId = match.SourceNodeId,
            ViaPredicateId = match.ViaPredicateId,
            ToolMatch = toolMatch,
        };
    }

    private static IReadOnlyList<McpGatewayGraphSearchEvidence> MapGraphSearchEvidence(
        IEnumerable<KnowledgeGraphSchemaSearchEvidence> evidenceItems
    )
    {
        var evidence = new List<McpGatewayGraphSearchEvidence>();
        foreach (var item in evidenceItems)
        {
            evidence.Add(
                new McpGatewayGraphSearchEvidence(
                    item.PredicateId,
                    item.MatchedText,
                    item.Kind.ToString(),
                    Math.Clamp(item.Score, 0d, 1d)
                )
                {
                    RelatedNodeId = item.RelatedNodeId,
                    RelatedNodeLabel = item.RelatedNodeLabel,
                    ViaPredicateId = item.ViaPredicateId,
                    ServiceEndpoint = item.ServiceEndpoint,
                    SourceContexts = MapGraphSearchSourceContexts(item.SourceContexts),
                }
            );
        }

        return evidence;
    }

    private static IReadOnlyList<McpGatewayGraphSearchSourceContext> MapGraphSearchSourceContexts(
        IEnumerable<KnowledgeGraphSchemaSearchSourceContext> sourceContexts
    )
    {
        var contexts = new List<McpGatewayGraphSearchSourceContext>();
        foreach (var sourceContext in sourceContexts)
        {
            contexts.Add(
                new McpGatewayGraphSearchSourceContext(
                    sourceContext.SourceId,
                    sourceContext.SourceLabel
                )
            );
        }

        return contexts;
    }

    private static McpGatewaySearchMatch? TryCreateGraphToolSearchMatch(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphSchemaSearchMatch match,
        SearchScoreContext scoreContext
    )
    {
        if (!TryResolveGraphToolEntry(graphIndex, match, out var entry))
        {
            return null;
        }

        var score = CalibrateGraphConfidence(
            entry,
            scoreContext,
            ApplySearchBoosts(entry, scoreContext, Math.Clamp(match.Score, 0d, 1d))
        );
        return ToSearchMatch(entry, score);
    }

    private static McpGatewaySearchMatch? TryCreateFocusedGraphToolSearchMatch(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphFocusedSearchMatch match,
        SearchScoreContext scoreContext
    )
    {
        if (!graphIndex.EntriesByNodeId.TryGetValue(match.NodeId, out var entry))
        {
            return null;
        }

        var score = CalibrateGraphConfidence(
            entry,
            scoreContext,
            ApplySearchBoosts(entry, scoreContext, Math.Clamp(match.Score, 0d, 1d))
        );
        return ToSearchMatch(entry, score);
    }

    private static bool TryResolveGraphToolEntry(
        ToolGraphSearchIndex graphIndex,
        KnowledgeGraphSchemaSearchMatch match,
        out ToolCatalogEntry entry
    )
    {
        if (graphIndex.EntriesByNodeId.TryGetValue(match.NodeId, out entry!))
        {
            return true;
        }

        if (
            match.SourceNodeId is not null
            && graphIndex.EntriesByNodeId.TryGetValue(match.SourceNodeId, out entry!)
        )
        {
            return true;
        }

        entry = null!;
        return false;
    }
}
