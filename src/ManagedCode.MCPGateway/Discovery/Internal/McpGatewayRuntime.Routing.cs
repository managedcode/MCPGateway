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
            score += RouteEnabledByDefaultScoreBoost;
        }

        if (preferReadOnly is true)
        {
            score += match.IsReadOnly switch
            {
                true => RouteReadOnlyPreferredScoreBoost,
                false => RouteWritableWhenReadOnlyPreferredScorePenalty,
                _ => RouteNoScoreAdjustment,
            };
        }
        else if (preferReadOnly is false)
        {
            score += match.IsReadOnly switch
            {
                false => RouteWritablePreferredScoreBoost,
                true => RouteReadOnlyWhenWritablePreferredScorePenalty,
                _ => RouteNoScoreAdjustment,
            };
            score += match.IsDestructive switch
            {
                true => RouteDestructiveWritableScoreBoost,
                false => RouteNonDestructiveWritableScoreBoost,
                _ => RouteNoScoreAdjustment,
            };
        }

        if (match.IsIdempotent is true)
        {
            score += RouteIdempotentScoreBoost;
        }

        score += match.CostTier switch
        {
            McpGatewayToolCostTier.Low => RouteLowCostScoreBoost,
            McpGatewayToolCostTier.Medium => RouteMediumCostScoreBoost,
            McpGatewayToolCostTier.High => RouteHighCostScorePenalty,
            _ => RouteNoScoreAdjustment,
        };
        score += match.LatencyTier switch
        {
            McpGatewayToolLatencyTier.Low => RouteLowLatencyScoreBoost,
            McpGatewayToolLatencyTier.Medium => RouteMediumLatencyScoreBoost,
            McpGatewayToolLatencyTier.High => RouteHighLatencyScorePenalty,
            _ => RouteNoScoreAdjustment,
        };

        if (match.Categories.Count > 0)
        {
            score += RouteCategorizedToolScoreBoost;
        }

        if (match.UsageExamples.Count > 0)
        {
            score += RouteUsageExampleScoreBoost;
        }

        return score;
    }

    private sealed record RoutedMatch(McpGatewaySearchMatch Match, double Score);
}
