using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static FocusedGraphSearch SearchFocusedGraphByRankedCandidates(
        ToolGraphSearchIndex graphIndex,
        string query,
        int limit,
        bool enableFuzzyTokenMatching,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rankedMatches = SearchGraphCandidates(
            graphIndex.CandidateSearch,
            query,
            Math.Max(1, limit),
            enableFuzzyTokenMatching
        );

        return CreateRankedFocusedGraphSearch(graphIndex, rankedMatches);
    }

    private static FocusedGraphSearch CreateRankedFocusedGraphSearch(
        ToolGraphSearchIndex graphIndex,
        IReadOnlyList<KnowledgeGraphRankedSearchMatch> rankedMatches
    )
    {
        if (rankedMatches.Count == 0)
        {
            return new FocusedGraphSearch([], [], [], KnowledgeGraphSnapshot.Empty, false);
        }

        var primary = CreatePrimaryRankedFocusedMatches(rankedMatches, graphIndex.NodesById);
        var related = ResolveRankedRelatedMatches(
            graphIndex,
            primary
        );
        var nextSteps = ResolveRankedNextStepMatches(
            graphIndex,
            primary
        );
        var focusedGraph = BuildRankedFocusedGraph(graphIndex, primary, related, nextSteps);
        return new FocusedGraphSearch(primary, related, nextSteps, focusedGraph, false);
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> CreatePrimaryRankedFocusedMatches(
        IEnumerable<KnowledgeGraphRankedSearchMatch> rankedMatches,
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById
    )
    {
        var matches = new List<KnowledgeGraphFocusedSearchMatch>();
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rankedMatch in rankedMatches)
        {
            if (!nodesById.ContainsKey(rankedMatch.NodeId) || !seenNodeIds.Add(rankedMatch.NodeId))
            {
                continue;
            }

            matches.Add(
                new KnowledgeGraphFocusedSearchMatch(
                    rankedMatch.NodeId,
                    rankedMatch.Label,
                    KnowledgeGraphFocusedSearchRole.Primary,
                    rankedMatch.Score
                )
            );
        }

        return matches;
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> ResolveRankedRelatedMatches(
        ToolGraphSearchIndex graphIndex,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> primary
    )
    {
        var primaryIds = CreateRankedFocusedMatchIdSet(primary);
        var matches = new Dictionary<string, KnowledgeGraphFocusedSearchMatch>(
            StringComparer.Ordinal
        );
        var groupIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var primaryMatch in primary)
        {
            if (
                graphIndex.Navigation.RelatedBySource.TryGetValue(
                    primaryMatch.NodeId,
                    out var relatedLinks
                )
            )
            {
                foreach (var link in relatedLinks)
                {
                    AddRankedFocusedMatch(
                        graphIndex.NodesById,
                        graphIndex.SearchableNodeIds,
                        matches,
                        link.NodeId,
                        KnowledgeGraphFocusedSearchRole.Related,
                        link.SourceNodeId,
                        link.ViaPredicateLabel,
                        link.Score
                    );
                }
            }

            if (
                graphIndex.Navigation.GroupIdsBySource.TryGetValue(
                    primaryMatch.NodeId,
                    out var primaryGroupIds
                )
            )
            {
                foreach (var groupId in primaryGroupIds)
                {
                    groupIds.Add(groupId);
                }
            }
        }

        AddSharedGroupRankedRelatedMatches(
            graphIndex,
            primaryIds,
            groupIds,
            matches
        );
        return SortRankedFocusedMatches(matches.Values, GraphFocusedRelatedResultsLimit);
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> ResolveRankedNextStepMatches(
        ToolGraphSearchIndex graphIndex,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> primary
    )
    {
        var matches = new Dictionary<string, KnowledgeGraphFocusedSearchMatch>(
            StringComparer.Ordinal
        );
        foreach (var primaryMatch in primary)
        {
            if (
                !graphIndex.Navigation.NextStepsBySource.TryGetValue(
                    primaryMatch.NodeId,
                    out var nextStepLinks
                )
            )
            {
                continue;
            }

            foreach (var link in nextStepLinks)
            {
                AddRankedFocusedMatch(
                    graphIndex.NodesById,
                    graphIndex.SearchableNodeIds,
                    matches,
                    link.NodeId,
                    KnowledgeGraphFocusedSearchRole.NextStep,
                    link.SourceNodeId,
                    link.ViaPredicateLabel,
                    link.Score
                );
            }
        }

        return SortRankedFocusedMatches(matches.Values, GraphFocusedNextStepResultsLimit);
    }

    private static void AddSharedGroupRankedRelatedMatches(
        ToolGraphSearchIndex graphIndex,
        IReadOnlySet<string> primaryIds,
        IReadOnlySet<string> groupIds,
        IDictionary<string, KnowledgeGraphFocusedSearchMatch> matches
    )
    {
        if (groupIds.Count == 0)
        {
            return;
        }

        foreach (var groupId in groupIds)
        {
            if (!graphIndex.Navigation.MembersByGroupId.TryGetValue(groupId, out var members))
            {
                continue;
            }

            foreach (var memberId in members)
            {
                if (primaryIds.Contains(memberId))
                {
                    continue;
                }

                AddRankedFocusedMatch(
                    graphIndex.NodesById,
                    graphIndex.SearchableNodeIds,
                    matches,
                    memberId,
                    KnowledgeGraphFocusedSearchRole.Related,
                    groupId,
                    KbPredicateMemberOf,
                    GraphRankedSharedGroupRelatedScore
                );
            }
        }
    }

    private static void AddRankedFocusedMatch(
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        IReadOnlySet<string> searchableNodeIds,
        IDictionary<string, KnowledgeGraphFocusedSearchMatch> matches,
        string nodeId,
        KnowledgeGraphFocusedSearchRole role,
        string sourceNodeId,
        string viaPredicateLabel,
        double score
    )
    {
        if (!searchableNodeIds.Contains(nodeId) || !nodesById.TryGetValue(nodeId, out var node))
        {
            return;
        }

        if (matches.TryGetValue(nodeId, out var existing) && existing.Score >= score)
        {
            return;
        }

        matches[nodeId] = new KnowledgeGraphFocusedSearchMatch(
            nodeId,
            node.Label,
            role,
            score,
            sourceNodeId,
            viaPredicateLabel
        );
    }

    private static KnowledgeGraphSnapshot BuildRankedFocusedGraph(
        ToolGraphSearchIndex graphIndex,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> primary,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> related,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> nextSteps
    )
    {
        var selectedIds = CreateRankedFocusedMatchIdSet(primary, related, nextSteps);
        var groupIds = SelectRankedFocusedGroupIds(graphIndex, selectedIds);
        var includedIds = CreateRankedFocusedIncludedNodeIds(selectedIds, groupIds);
        var nodes = SelectRankedFocusedNodes(graphIndex.NodesById, includedIds);
        var edges = SelectRankedFocusedEdges(graphIndex.Navigation, selectedIds, groupIds);
        return new KnowledgeGraphSnapshot(nodes, edges);
    }

    private static IReadOnlyList<KnowledgeGraphNode> SelectRankedFocusedNodes(
        IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
        IReadOnlySet<string> includedIds
    )
    {
        var nodes = new List<KnowledgeGraphNode>(includedIds.Count);
        foreach (var nodeId in includedIds)
        {
            if (nodesById.TryGetValue(nodeId, out var node))
            {
                nodes.Add(node);
            }
        }

        nodes.Sort(
            static (left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal)
        );
        return nodes.ToArray();
    }

    private static IReadOnlyList<KnowledgeGraphEdge> SelectRankedFocusedEdges(
        GraphNavigationIndex navigation,
        IReadOnlySet<string> selectedIds,
        IReadOnlySet<string> groupIds
    )
    {
        var edges = new List<KnowledgeGraphEdge>();
        foreach (var subjectId in selectedIds)
        {
            if (!navigation.EdgesBySubject.TryGetValue(subjectId, out var subjectEdges))
            {
                continue;
            }

            foreach (var edge in subjectEdges)
            {
                if (
                    selectedIds.Contains(edge.ObjectId)
                    || (
                        groupIds.Contains(edge.ObjectId)
                        && IsGraphPredicate(edge, KbPredicateMemberOf)
                    )
                )
                {
                    edges.Add(edge);
                }
            }
        }

        edges.Sort(CompareRankedGraphEdges);
        return edges.ToArray();
    }

    private static IReadOnlySet<string> SelectRankedFocusedGroupIds(
        ToolGraphSearchIndex graphIndex,
        IReadOnlySet<string> selectedIds
    )
    {
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selectedId in selectedIds)
        {
            if (!graphIndex.Navigation.GroupIdsBySource.TryGetValue(selectedId, out var groups))
            {
                continue;
            }

            foreach (var groupId in groups)
            {
                groupIds.Add(groupId);
            }
        }

        return groupIds;
    }

    private static IReadOnlySet<string> CreateRankedFocusedIncludedNodeIds(
        IReadOnlySet<string> selectedIds,
        IReadOnlySet<string> groupIds
    )
    {
        var includedIds = new HashSet<string>(selectedIds.Count + groupIds.Count, StringComparer.Ordinal);
        includedIds.UnionWith(selectedIds);
        includedIds.UnionWith(groupIds);
        return includedIds;
    }

    private static HashSet<string> CreateRankedFocusedMatchIdSet(
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> matches
    )
    {
        var ids = new HashSet<string>(matches.Count, StringComparer.Ordinal);
        foreach (var match in matches)
        {
            ids.Add(match.NodeId);
        }

        return ids;
    }

    private static IReadOnlySet<string> CreateRankedFocusedMatchIdSet(
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> primary,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> related,
        IReadOnlyList<KnowledgeGraphFocusedSearchMatch> nextSteps
    )
    {
        var ids = new HashSet<string>(
            primary.Count + related.Count + nextSteps.Count,
            StringComparer.Ordinal
        );
        AddRankedFocusedMatchIds(ids, primary);
        AddRankedFocusedMatchIds(ids, related);
        AddRankedFocusedMatchIds(ids, nextSteps);
        return ids;
    }

    private static void AddRankedFocusedMatchIds(
        ISet<string> ids,
        IEnumerable<KnowledgeGraphFocusedSearchMatch> matches
    )
    {
        foreach (var match in matches)
        {
            ids.Add(match.NodeId);
        }
    }

    private static IReadOnlyList<KnowledgeGraphFocusedSearchMatch> SortRankedFocusedMatches(
        IEnumerable<KnowledgeGraphFocusedSearchMatch> matches,
        int maxResults
    )
    {
        if (maxResults == 0)
        {
            return [];
        }

        var sorted = new List<KnowledgeGraphFocusedSearchMatch>();
        foreach (var match in matches)
        {
            sorted.Add(match);
        }

        sorted.Sort(CompareRankedFocusedMatches);
        if (sorted.Count > maxResults)
        {
            sorted.RemoveRange(maxResults, sorted.Count - maxResults);
        }

        return sorted.ToArray();
    }

    private static IReadOnlyDictionary<string, KnowledgeGraphNode> CreateRankedGraphNodesById(
        IReadOnlyList<KnowledgeGraphNode> nodes
    )
    {
        var nodesById = new Dictionary<string, KnowledgeGraphNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            nodesById[node.Id] = node;
        }

        return nodesById;
    }

    private static GraphNavigationIndex CreateGraphNavigationIndex(
        KnowledgeGraphSnapshot snapshot,
        IReadOnlySet<string> searchableNodeIds
    )
    {
        if (snapshot.Edges.Count == 0 || searchableNodeIds.Count == 0)
        {
            return GraphNavigationIndex.Empty;
        }

        var edgesBySubject = new Dictionary<string, List<KnowledgeGraphEdge>>(
            StringComparer.Ordinal
        );
        var relatedBySource = new Dictionary<string, List<GraphNavigationLink>>(
            StringComparer.Ordinal
        );
        var nextStepsBySource = new Dictionary<string, List<GraphNavigationLink>>(
            StringComparer.Ordinal
        );
        var groupIdsBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var membersByGroupId = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var edge in snapshot.Edges)
        {
            AddGraphEdge(edgesBySubject, edge);
            if (!searchableNodeIds.Contains(edge.SubjectId))
            {
                continue;
            }

            if (IsGraphPredicate(edge, KbPredicateRelatedTo))
            {
                AddGraphNavigationLink(
                    relatedBySource,
                    edge.SubjectId,
                    new GraphNavigationLink(
                        edge.ObjectId,
                        edge.SubjectId,
                        edge.PredicateLabel,
                        GraphNavigationRelatedScore
                    )
                );
            }
            else if (IsGraphPredicate(edge, KbPredicateNextStep))
            {
                AddGraphNavigationLink(
                    nextStepsBySource,
                    edge.SubjectId,
                    new GraphNavigationLink(
                        edge.ObjectId,
                        edge.SubjectId,
                        edge.PredicateLabel,
                        GraphNavigationNextStepScore
                    )
                );
            }
            else if (IsGraphPredicate(edge, KbPredicateMemberOf))
            {
                AddGraphStringValue(groupIdsBySource, edge.SubjectId, edge.ObjectId);
                AddGraphStringValue(membersByGroupId, edge.ObjectId, edge.SubjectId);
            }
        }

        return new GraphNavigationIndex(
            FreezeGraphEdgeDictionary(edgesBySubject),
            FreezeGraphNavigationDictionary(relatedBySource),
            FreezeGraphNavigationDictionary(nextStepsBySource),
            FreezeGraphStringDictionary(groupIdsBySource),
            FreezeGraphStringDictionary(membersByGroupId)
        );
    }

    private static void AddGraphEdge(
        IDictionary<string, List<KnowledgeGraphEdge>> edgesBySubject,
        KnowledgeGraphEdge edge
    )
    {
        if (!edgesBySubject.TryGetValue(edge.SubjectId, out var edges))
        {
            edges = [];
            edgesBySubject[edge.SubjectId] = edges;
        }

        edges.Add(edge);
    }

    private static void AddGraphNavigationLink(
        IDictionary<string, List<GraphNavigationLink>> linksBySource,
        string sourceNodeId,
        GraphNavigationLink link
    )
    {
        if (!linksBySource.TryGetValue(sourceNodeId, out var links))
        {
            links = [];
            linksBySource[sourceNodeId] = links;
        }

        links.Add(link);
    }

    private static void AddGraphStringValue(
        IDictionary<string, List<string>> valuesByKey,
        string key,
        string value
    )
    {
        if (!valuesByKey.TryGetValue(key, out var values))
        {
            values = [];
            valuesByKey[key] = values;
        }

        values.Add(value);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<KnowledgeGraphEdge>> FreezeGraphEdgeDictionary(
        IReadOnlyDictionary<string, List<KnowledgeGraphEdge>> source
    )
    {
        var result = new Dictionary<string, IReadOnlyList<KnowledgeGraphEdge>>(
            source.Count,
            StringComparer.Ordinal
        );
        foreach (var (key, values) in source)
        {
            result[key] = values.ToArray();
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<GraphNavigationLink>> FreezeGraphNavigationDictionary(
        IReadOnlyDictionary<string, List<GraphNavigationLink>> source
    )
    {
        var result = new Dictionary<string, IReadOnlyList<GraphNavigationLink>>(
            source.Count,
            StringComparer.Ordinal
        );
        foreach (var (key, values) in source)
        {
            result[key] = values.ToArray();
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> FreezeGraphStringDictionary(
        IReadOnlyDictionary<string, List<string>> source
    )
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(
            source.Count,
            StringComparer.Ordinal
        );
        foreach (var (key, values) in source)
        {
            result[key] = values.ToArray();
        }

        return result;
    }

    private static bool IsGraphPredicate(KnowledgeGraphEdge edge, string predicate) =>
        string.Equals(edge.PredicateLabel, predicate, StringComparison.Ordinal)
        || string.Equals(edge.PredicateId, predicate, StringComparison.Ordinal);

    private static int CompareRankedFocusedMatches(
        KnowledgeGraphFocusedSearchMatch left,
        KnowledgeGraphFocusedSearchMatch right
    )
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        return scoreComparison != 0
            ? scoreComparison
            : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareRankedGraphEdges(KnowledgeGraphEdge left, KnowledgeGraphEdge right)
    {
        var subjectComparison = string.Compare(left.SubjectId, right.SubjectId, StringComparison.Ordinal);
        if (subjectComparison != 0)
        {
            return subjectComparison;
        }

        var predicateComparison = string.Compare(
            left.PredicateId,
            right.PredicateId,
            StringComparison.Ordinal
        );
        return predicateComparison != 0
            ? predicateComparison
            : string.Compare(left.ObjectId, right.ObjectId, StringComparison.Ordinal);
    }

}
