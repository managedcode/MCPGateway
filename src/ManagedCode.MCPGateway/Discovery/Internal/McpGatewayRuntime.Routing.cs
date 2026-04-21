namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private const int DefaultRouteCategoryLimit = 5;
    private const int DefaultRouteToolsPerCategory = 3;
    private const int MaxRouteCategoryLimit = 20;
    private const string GeneralRouteCategory = "general";

    public async Task<McpGatewayToolRouteResult> RouteToolsAsync(
        McpGatewayToolRouteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxCategories = Math.Clamp(
            request.MaxCategories.GetValueOrDefault(DefaultRouteCategoryLimit),
            1,
            MaxRouteCategoryLimit
        );
        var maxToolsPerCategory = Math.Clamp(
            request.MaxToolsPerCategory.GetValueOrDefault(DefaultRouteToolsPerCategory),
            1,
            _maxSearchResults
        );
        var candidateLimit = Math.Clamp(
            maxCategories * Math.Max(maxToolsPerCategory, DefaultRouteToolsPerCategory) * 4,
            maxCategories,
            _maxSearchResults
        );

        var searchResult = await SearchAsync(
            new McpGatewaySearchRequest(
                Query: request.Query,
                MaxResults: candidateLimit,
                Context: request.Context,
                ContextSummary: request.ContextSummary,
                IncludeDisabledTools: request.IncludeDisabledTools
            ),
            cancellationToken
        );
        var preferReadOnly = request.PreferReadOnly ?? InferReadOnlyPreference(request);

        var routedCandidates = searchResult
            .Matches.Where(match => request.IncludeDisabledTools || match.IsEnabledByDefault)
            .Select(match => new RoutedMatch(match, CalculateRouteScore(match, preferReadOnly)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Match.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var categories = BuildRouteCategories(routedCandidates, maxCategories, maxToolsPerCategory);
        var suggestedMatches = categories
            .SelectMany(static category => category.Tools)
            .DistinctBy(static match => match.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new McpGatewayToolRouteResult(
            categories,
            suggestedMatches,
            searchResult.Diagnostics,
            searchResult.RankingMode
        );
    }

    private static IReadOnlyList<McpGatewayToolRouteCategory> BuildRouteCategories(
        IReadOnlyList<RoutedMatch> routedCandidates,
        int maxCategories,
        int maxToolsPerCategory
    )
    {
        var grouped = new Dictionary<string, List<RoutedMatch>>(StringComparer.OrdinalIgnoreCase);
        foreach (var routedCandidate in routedCandidates)
        {
            foreach (var category in ResolveRouteCategories(routedCandidate.Match))
            {
                if (!grouped.TryGetValue(category, out var matches))
                {
                    matches = [];
                    grouped[category] = matches;
                }

                matches.Add(routedCandidate);
            }
        }

        return grouped
            .Select(pair => new McpGatewayToolRouteCategory(
                pair.Key,
                pair.Value.Max(static candidate => candidate.Score),
                pair.Value.OrderByDescending(static candidate => candidate.Score)
                    .ThenBy(
                        static candidate => candidate.Match.ToolName,
                        StringComparer.OrdinalIgnoreCase
                    )
                    .Select(static candidate => candidate.Match)
                    .DistinctBy(static match => match.ToolId, StringComparer.OrdinalIgnoreCase)
                    .Take(maxToolsPerCategory)
                    .ToList()
            ))
            .OrderByDescending(static category => category.Score)
            .ThenBy(static category => category.Category, StringComparer.OrdinalIgnoreCase)
            .Take(maxCategories)
            .ToList();
    }

    private static IEnumerable<string> ResolveRouteCategories(McpGatewaySearchMatch match)
    {
        if (match.Categories.Count > 0)
        {
            foreach (var category in match.Categories)
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    yield return category.Trim();
                }
            }

            yield break;
        }

        if (match.DataSources.Count > 0)
        {
            foreach (var dataSource in match.DataSources)
            {
                if (!string.IsNullOrWhiteSpace(dataSource))
                {
                    yield return dataSource.Trim();
                }
            }

            yield break;
        }

        yield return string.IsNullOrWhiteSpace(match.SourceId)
            ? GeneralRouteCategory
            : match.SourceId;
    }

    private static bool? InferReadOnlyPreference(McpGatewayToolRouteRequest request)
    {
        var terms = BuildOrderedGraphTerms(
                string.Concat(request.Query, " ", request.ContextSummary)
            )
            .ToArray();
        if (terms.Any(GraphActionTerms.Contains))
        {
            return false;
        }

        if (terms.Any(GraphDiscoveryTerms.Contains) || terms.Any(GraphInspectionTerms.Contains))
        {
            return true;
        }

        return null;
    }

    private static double CalculateRouteScore(McpGatewaySearchMatch match, bool? preferReadOnly)
    {
        var score = match.Score;
        if (match.IsEnabledByDefault)
        {
            score += 0.02d;
        }

        if (preferReadOnly is true)
        {
            score += match.IsReadOnly switch
            {
                true => 0.08d,
                false => -0.08d,
                _ => 0d,
            };
        }
        else if (preferReadOnly is false)
        {
            score += match.IsReadOnly switch
            {
                false => 0.05d,
                true => -0.03d,
                _ => 0d,
            };
            score += match.IsDestructive switch
            {
                true => 0.02d,
                false => 0.01d,
                _ => 0d,
            };
        }

        if (match.IsIdempotent is true)
        {
            score += 0.01d;
        }

        score += match.CostTier switch
        {
            McpGatewayToolCostTier.Low => 0.03d,
            McpGatewayToolCostTier.Medium => 0.01d,
            McpGatewayToolCostTier.High => -0.02d,
            _ => 0d,
        };
        score += match.LatencyTier switch
        {
            McpGatewayToolLatencyTier.Low => 0.03d,
            McpGatewayToolLatencyTier.Medium => 0.01d,
            McpGatewayToolLatencyTier.High => -0.02d,
            _ => 0d,
        };

        if (match.Categories.Count > 0)
        {
            score += 0.01d;
        }

        if (match.UsageExamples.Count > 0)
        {
            score += 0.01d;
        }

        return score;
    }

    private sealed record RoutedMatch(McpGatewaySearchMatch Match, double Score);
}
