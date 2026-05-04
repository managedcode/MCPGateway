using System.Globalization;
using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    public async Task<McpGatewayGraphSchemaResult> DescribeGraphSchemaAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<McpGatewayDiagnostic>();
        var profile = CreateToolGraphSchemaSearchProfile(
            _defaultSearchLimit,
            GraphFocusedRelatedResultsLimit,
            GraphFocusedNextStepResultsLimit
        );

        if (snapshot.GraphIndex is ToolGraphSearchIndex graphIndex)
        {
            AddSchemaProfileDiagnostics(graphIndex.SchemaDiagnostics, diagnostics);
            return MapGraphSchemaResult(graphIndex, profile, diagnostics);
        }

        diagnostics.Add(
            new McpGatewayDiagnostic(GraphUnavailableDiagnosticCode, GraphUnavailableMessage)
        );
        return MapGraphSchemaResult(null, profile, diagnostics);
    }

    private static KnowledgeGraphSchemaSearchProfile CreateToolGraphSchemaSearchProfile(
        int primaryLimit,
        int relatedLimit,
        int nextStepLimit,
        IReadOnlyList<Uri>? serviceEndpoints = null
    ) =>
        new()
        {
            Prefixes = CreateToolGraphSchemaPrefixes(),
            TextPredicates =
            [
                new KnowledgeGraphSchemaTextPredicate(
                    SchemaPredicateName,
                    GraphSchemaNameTextWeight
                ),
                new KnowledgeGraphSchemaTextPredicate(
                    SchemaPredicateDescription,
                    GraphSchemaDescriptionTextWeight
                ),
                new KnowledgeGraphSchemaTextPredicate(
                    SchemaPredicateKeywords,
                    GraphSchemaKeywordsTextWeight
                ),
                new KnowledgeGraphSchemaTextPredicate(
                    SkosPredicatePrefLabel,
                    GraphSchemaPrefLabelTextWeight
                ),
            ],
            RelationshipPredicates =
            [
                new KnowledgeGraphSchemaRelationshipPredicate(
                    SchemaPredicateAbout,
                    [SchemaPredicateName, SkosPredicatePrefLabel],
                    GraphSchemaAboutRelationshipWeight
                ),
                new KnowledgeGraphSchemaRelationshipPredicate(
                    SchemaPredicateMentions,
                    [SchemaPredicateName],
                    GraphSchemaMentionsRelationshipWeight
                ),
                new KnowledgeGraphSchemaRelationshipPredicate(
                    KbPredicateMemberOf,
                    [SchemaPredicateName, SkosPredicatePrefLabel],
                    GraphSchemaGroupMembershipRelationshipWeight
                ),
                new KnowledgeGraphSchemaRelationshipPredicate(
                    ProvPredicateWasDerivedFrom,
                    [SchemaPredicateName, SchemaPredicateDescription],
                    GraphSchemaProvenanceRelationshipWeight
                )
                {
                    Direction = KnowledgeGraphSchemaRelationshipDirection.Inbound,
                },
            ],
            ExpansionPredicates =
            [
                new KnowledgeGraphSchemaExpansionPredicate(
                    KbPredicateRelatedTo,
                    KnowledgeGraphSchemaSearchRole.Related,
                    GraphSchemaRelatedExpansionScore
                ),
                new KnowledgeGraphSchemaExpansionPredicate(
                    KbPredicateNextStep,
                    KnowledgeGraphSchemaSearchRole.NextStep,
                    GraphSchemaNextStepExpansionScore
                ),
                new KnowledgeGraphSchemaExpansionPredicate(
                    SchemaPredicateMentions,
                    KnowledgeGraphSchemaSearchRole.Related,
                    GraphSchemaMentionsExpansionScore
                ),
                new KnowledgeGraphSchemaExpansionPredicate(
                    SchemaPredicateHasPart,
                    KnowledgeGraphSchemaSearchRole.Related,
                    GraphSchemaHasPartExpansionScore
                ),
            ],
            TypeFilters = [SchemaTypeArticle, KbTypeMarkdownDocument],
            TermMode = KnowledgeGraphSchemaSearchTermMode.AnyTerm,
            MaxResults = primaryLimit,
            MaxRelatedResults = relatedLimit,
            MaxNextStepResults = nextStepLimit,
            FederatedServiceEndpoints = serviceEndpoints ?? [],
        };

    private McpGatewayGraphSchemaResult MapGraphSchemaResult(
        ToolGraphSearchIndex? graphIndex,
        KnowledgeGraphSchemaSearchProfile profile,
        IReadOnlyList<McpGatewayDiagnostic> diagnostics
    ) =>
        new(
            _searchStrategy,
            _markdownLdGraphSearchMode,
            _markdownLdGraphSource,
            graphIndex is not null,
            graphIndex?.CanSearchByTokenDistance ?? false,
            graphIndex?.NodeCount ?? 0,
            graphIndex?.EdgeCount ?? 0,
            _defaultSearchLimit,
            _maxSearchResults,
            new Dictionary<string, string>(profile.Prefixes, StringComparer.Ordinal),
            MapGraphSchemaTextPredicates(profile.TextPredicates),
            MapGraphSchemaRelationshipPredicates(profile.RelationshipPredicates),
            MapGraphSchemaExpansionPredicates(profile.ExpansionPredicates),
            profile.TypeFilters.ToArray(),
            profile.TermMode.ToString(),
            profile.MaxResults,
            profile.MaxRelatedResults,
            profile.MaxNextStepResults,
            _markdownLdFederatedServiceEndpoints
                .Select(static endpoint => endpoint.AbsoluteUri)
                .ToArray(),
            diagnostics
        );

    private static IReadOnlyList<McpGatewayGraphSchemaTextPredicate> MapGraphSchemaTextPredicates(
        IEnumerable<KnowledgeGraphSchemaTextPredicate> predicates
    )
    {
        var mappedPredicates = new List<McpGatewayGraphSchemaTextPredicate>();
        foreach (var predicate in predicates)
        {
            mappedPredicates.Add(
                new McpGatewayGraphSchemaTextPredicate(predicate.Predicate, predicate.Weight)
            );
        }

        return mappedPredicates;
    }

    private static IReadOnlyList<
        McpGatewayGraphSchemaRelationshipPredicate
    > MapGraphSchemaRelationshipPredicates(
        IEnumerable<KnowledgeGraphSchemaRelationshipPredicate> predicates
    )
    {
        var mappedPredicates = new List<McpGatewayGraphSchemaRelationshipPredicate>();
        foreach (var predicate in predicates)
        {
            mappedPredicates.Add(
                new McpGatewayGraphSchemaRelationshipPredicate(
                    predicate.Predicate,
                    predicate.TargetTextPredicates.ToArray(),
                    predicate.Weight,
                    predicate.Direction.ToString()
                )
            );
        }

        return mappedPredicates;
    }

    private static IReadOnlyList<
        McpGatewayGraphSchemaExpansionPredicate
    > MapGraphSchemaExpansionPredicates(
        IEnumerable<KnowledgeGraphSchemaExpansionPredicate> predicates
    )
    {
        var mappedPredicates = new List<McpGatewayGraphSchemaExpansionPredicate>();
        foreach (var predicate in predicates)
        {
            mappedPredicates.Add(
                new McpGatewayGraphSchemaExpansionPredicate(
                    predicate.Predicate,
                    predicate.Role.ToString(),
                    predicate.Score
                )
            );
        }

        return mappedPredicates;
    }

    private static IReadOnlyDictionary<string, string> CreateToolGraphSchemaPrefixes() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchemaPrefixName] = SchemaPrefixUri,
            [KbPrefixName] = KbPrefixUri,
            [SkosPrefixName] = SkosPrefixUri,
            [ProvPrefixName] = ProvPrefixUri,
        };

    private static string NormalizeGraphSchemaQuery(string graphQuery)
    {
        var schemaTerms = BuildOrderedGraphTerms(
            graphQuery,
            GraphSchemaQueryMaxTerms,
            IsGraphSchemaSearchQueryTerm
        );
        return schemaTerms.Count == 0 ? graphQuery : string.Join(' ', schemaTerms);
    }

    private static string NormalizeGraphSchemaQuery(
        ToolGraphSearchIndex graphIndex,
        string graphQuery
    )
    {
        var schemaTerms = BuildOrderedGraphTerms(
            graphQuery,
            GraphSchemaQueryMaxTerms,
            IsGraphSchemaSearchQueryTerm
        );
        if (schemaTerms.Count == 0)
        {
            return graphQuery;
        }

        if (!ShouldUseCandidateSchemaSearch(graphIndex))
        {
            return string.Join(' ', schemaTerms);
        }

        var selectedTerms = SelectGraphSchemaQueryTerms(graphIndex.CandidateSearch, schemaTerms);
        return string.Join(' ', selectedTerms);
    }

    private static IReadOnlyList<string> SelectGraphSchemaQueryTerms(
        GraphCandidateSearchIndex candidateSearch,
        IReadOnlyList<string> schemaTerms
    )
    {
        if (schemaTerms.Count <= 1)
        {
            return schemaTerms;
        }

        var scoredTerms = new List<GraphSchemaQueryTerm>(schemaTerms.Count);
        var hasDistinctiveTerm = false;
        for (var index = 0; index < schemaTerms.Count; index++)
        {
            var term = schemaTerms[index];
            var weight = ResolveGraphCandidateTermWeight(
                term,
                candidateSearch.InverseDocumentFrequency
            );
            hasDistinctiveTerm |= weight >= GraphSchemaDistinctiveTermMinimumWeight;
            scoredTerms.Add(new GraphSchemaQueryTerm(term, index, weight));
        }

        var maxTerms = hasDistinctiveTerm
            ? GraphSchemaDistinctiveQueryMaxTerms
            : GraphSchemaBroadQueryMaxTerms;
        scoredTerms.Sort(CompareGraphSchemaQueryTermsByWeight);
        if (scoredTerms.Count > maxTerms)
        {
            scoredTerms.RemoveRange(maxTerms, scoredTerms.Count - maxTerms);
        }

        scoredTerms.Sort(CompareGraphSchemaQueryTermsByOriginalIndex);
        var selectedTerms = new string[scoredTerms.Count];
        for (var index = 0; index < scoredTerms.Count; index++)
        {
            selectedTerms[index] = scoredTerms[index].Value;
        }

        return selectedTerms;
    }

    private static int CompareGraphSchemaQueryTermsByWeight(
        GraphSchemaQueryTerm left,
        GraphSchemaQueryTerm right
    )
    {
        var weightComparison = right.Weight.CompareTo(left.Weight);
        return weightComparison != 0
            ? weightComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int CompareGraphSchemaQueryTermsByOriginalIndex(
        GraphSchemaQueryTerm left,
        GraphSchemaQueryTerm right
    ) => left.OriginalIndex.CompareTo(right.OriginalIndex);

    private static bool IsGraphSchemaSearchQueryTerm(string term) =>
        !GraphGenericTerms.Contains(term)
        && !GraphDiscoveryTerms.Contains(term)
        && !GraphInspectionTerms.Contains(term)
        && !GraphActionTerms.Contains(term);

    private static IReadOnlyList<McpGatewayDiagnostic> CreateSchemaProfileDiagnostics(
        KnowledgeGraph graph,
        KnowledgeGraphSchemaSearchProfile profile
    )
    {
        var validation = graph.ValidateSchemaSearchProfile(profile);
        if (validation.IsValid)
        {
            return [];
        }

        return
        [
            new(
                GraphSchemaValidationDiagnosticCode,
                string.Format(
                    CultureInfo.InvariantCulture,
                    GraphSchemaValidationMessageFormat,
                    string.Join("; ", validation.Issues)
                )
            ),
        ];
    }

    private static void AddSchemaProfileDiagnostics(
        IReadOnlyList<McpGatewayDiagnostic> source,
        IList<McpGatewayDiagnostic> target
    )
    {
        foreach (var diagnostic in source)
        {
            target.Add(diagnostic);
        }
    }
}
