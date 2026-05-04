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

        var routedCandidates = new List<RoutedMatch>(searchResult.Matches.Count);
        foreach (var match in searchResult.Matches)
        {
            if (!request.IncludeDisabledTools && !match.IsEnabledByDefault)
            {
                continue;
            }

            routedCandidates.Add(new RoutedMatch(match, CalculateRouteScore(match, preferReadOnly)));
        }

        routedCandidates.Sort(CompareRoutedMatches);

        var categories = BuildRouteCategories(routedCandidates, maxCategories, maxToolsPerCategory);
        var suggestedMatches = BuildSuggestedRouteMatches(categories);

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

        var categories = new List<McpGatewayToolRouteCategory>(grouped.Count);
        foreach (var (category, candidates) in grouped)
        {
            candidates.Sort(CompareRoutedMatches);
            categories.Add(
                new McpGatewayToolRouteCategory(
                    category,
                    candidates[0].Score,
                    SelectRouteCategoryTools(candidates, maxToolsPerCategory)
                )
            );
        }

        categories.Sort(CompareRouteCategories);
        if (categories.Count > maxCategories)
        {
            categories.RemoveRange(maxCategories, categories.Count - maxCategories);
        }

        return categories;
    }

    private static IReadOnlyList<McpGatewaySearchMatch> SelectRouteCategoryTools(
        IReadOnlyList<RoutedMatch> candidates,
        int maxToolsPerCategory
    )
    {
        var tools = new List<McpGatewaySearchMatch>(Math.Min(candidates.Count, maxToolsPerCategory));
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!seenToolIds.Add(candidate.Match.ToolId))
            {
                continue;
            }

            tools.Add(candidate.Match);
            if (tools.Count >= maxToolsPerCategory)
            {
                break;
            }
        }

        return tools;
    }

    private static IReadOnlyList<McpGatewaySearchMatch> BuildSuggestedRouteMatches(
        IReadOnlyList<McpGatewayToolRouteCategory> categories
    )
    {
        var suggestedMatches = new List<McpGatewaySearchMatch>();
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            foreach (var match in category.Tools)
            {
                if (seenToolIds.Add(match.ToolId))
                {
                    suggestedMatches.Add(match);
                }
            }
        }

        return suggestedMatches;
    }

    private static int CompareRoutedMatches(RoutedMatch left, RoutedMatch right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        return scoreComparison != 0
            ? scoreComparison
            : string.Compare(left.Match.ToolName, right.Match.ToolName, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareRouteCategories(
        McpGatewayToolRouteCategory left,
        McpGatewayToolRouteCategory right
    )
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        return scoreComparison != 0
            ? scoreComparison
            : string.Compare(left.Category, right.Category, StringComparison.OrdinalIgnoreCase);
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
        var terms = BuildOrderedGraphTerms(string.Concat(request.Query, " ", request.ContextSummary));
        if (ContainsAnyGraphTerm(terms, GraphActionTerms))
        {
            return false;
        }

        if (
            ContainsAnyGraphTerm(terms, GraphDiscoveryTerms)
            || ContainsAnyGraphTerm(terms, GraphInspectionTerms)
        )
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

    private readonly record struct RoutedMatch(McpGatewaySearchMatch Match, double Score);
}
