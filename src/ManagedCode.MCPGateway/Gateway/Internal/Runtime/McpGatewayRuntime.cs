using System.Text;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime : IMcpGateway, IMcpGatewayGraphSearch
{
    private const string QueryArgumentName = "query";
    private const string ContextArgumentName = "context";
    private const string ContextSummaryArgumentName = "contextSummary";
    private const string GatewayInvocationMetaKey = "managedCodeMcpGateway";
    private const string SearchModeEmpty = "empty";
    private const string SearchModeBrowse = "browse";
    private const string SearchModeGraph = "graph";
    private const string SearchModeHybrid = "hybrid";
    private const string SearchModeVector = "vector";
    private const string SourceLoadFailedDiagnosticCode = "source_load_failed";
    private const string DuplicateToolIdDiagnosticCode = "duplicate_tool_id";
    private const string GraphBuildFailedDiagnosticCode = "graph_build_failed";
    private const string GraphFallbackDiagnosticCode = "graph_fallback";
    private const string LowConfidenceResultsDiagnosticCode = "low_confidence_results";
    private const string HybridVectorMergeDiagnosticCode = "hybrid_vector_merge_used";
    private const string GraphUnavailableDiagnosticCode = "graph_unavailable";
    private const string GraphSearchFailedDiagnosticCode = "graph_search_failed";
    private const string GraphSchemaFallbackDiagnosticCode = "graph_schema_fallback";
    private const string GraphSchemaValidationDiagnosticCode = "graph_schema_profile_invalid";
    private const string GraphFederationEndpointBlockedDiagnosticCode =
        "graph_federation_endpoint_blocked";
    private const string GraphFederationEndpointInvalidDiagnosticCode =
        "graph_federation_endpoint_invalid";
    private const string EmbeddingCountMismatchDiagnosticCode = "embedding_count_mismatch";
    private const string EmbeddingGeneratorMissingDiagnosticCode = "embedding_generator_missing";
    private const string EmbeddingFailedDiagnosticCode = "embedding_failed";
    private const string EmbeddingStoreLoadFailedDiagnosticCode = "embedding_store_load_failed";
    private const string EmbeddingStoreSaveFailedDiagnosticCode = "embedding_store_save_failed";
    private const string QueryVectorEmptyDiagnosticCode = "query_vector_empty";
    private const string QueryNormalizedDiagnosticCode = "query_normalized";
    private const string QueryNormalizationFailedDiagnosticCode = "query_normalization_failed";
    private const string VectorSearchFailedDiagnosticCode = "vector_search_failed";
    private const string MarkdownLdGraphPathMissingDiagnosticCode =
        "markdown_ld_graph_path_missing";
    private const string MarkdownLdGraphDocumentFactoryMissingDiagnosticCode =
        "markdown_ld_graph_document_factory_missing";
    private const string SourceLoadFailedMessageTemplate =
        "Failed to load tools from source '{0}': {1}";
    private const string DuplicateToolIdMessageTemplate = "Skipped duplicate tool id '{0}'.";
    private const string GraphBuildFailedMessageTemplate =
        "Building the Markdown-LD tool graph failed: {0}";
    private const string GraphFallbackMessage =
        "Vector search was unavailable or unusable. Markdown-LD graph ranking was used.";
    private const string LowConfidenceResultsMessage =
        "Graph ranking confidence was low for this query.";
    private const string HybridVectorMergeMessage =
        "Vector-first ranking was supplemented with Markdown-LD graph expansion.";
    private const string GraphUnavailableMessage = "Markdown-LD graph ranking is unavailable.";
    private const string GraphSearchFailedMessageTemplate = "Markdown-LD graph ranking failed: {0}";
    private const string GraphSchemaFallbackMessage =
        "Markdown-LD schema-aware ranking found no mapped gateway tools. Ranked BM25 graph ranking with fuzzy token matching was used.";
    private const string GraphSchemaTokenDistanceFallbackMessage =
        "Markdown-LD schema-aware ranking found no mapped gateway tools. Token-distance graph ranking was used.";
    private const string GraphSchemaNoSupplementMessage =
        "Markdown-LD schema-aware ranking found no mapped gateway tools. No graph supplement was added.";
    private const string GraphSchemaValidationMessageTemplate =
        "Markdown-LD schema search profile is invalid: {0}";
    private const string GraphFederationEndpointBlockedMessageTemplate =
        "Markdown-LD federated service endpoint '{0}' is not configured in the gateway allowlist.";
    private const string GraphFederationEndpointInvalidMessageTemplate =
        "Markdown-LD federated service endpoint '{0}' is not an absolute URI.";
    private const string EmbeddingCountMismatchMessageTemplate =
        "Embedding generation returned {0} vectors for {1} tools.";
    private const string QueryEmbeddingCountMismatchMessageTemplate =
        "Embedding generation returned {0} vectors for {1} search query.";
    private const string EmbeddingGeneratorMissingMessage =
        "No keyed or unkeyed IEmbeddingGenerator<string, Embedding<float>> is registered. Stored tool embeddings may be reused, but search falls back to Markdown-LD graph ranking without a query embedding generator.";
    private const string EmbeddingFailedMessageTemplate = "Embedding generation failed: {0}";
    private const string EmbeddingStoreLoadFailedMessageTemplate =
        "Loading stored tool embeddings failed: {0}";
    private const string EmbeddingStoreSaveFailedMessageTemplate =
        "Persisting generated tool embeddings failed: {0}";
    private const string QueryVectorEmptyMessage =
        "Embedding generator returned an empty query vector.";
    private const string QueryNormalizedMessage =
        "Search query was normalized to English before ranking.";
    private const string QueryNormalizationFailedMessageTemplate =
        "Search query normalization failed and the original query was used: {0}";
    private const string VectorSearchFailedMessageTemplate =
        "Vector ranking failed and Markdown-LD graph fallback was used: {0}";
    private const string MarkdownLdGraphPathMissingMessage =
        "Markdown-LD graph file mode requires MarkdownLdGraphPath to point to a graph bundle file, Markdown source file, or directory.";
    private const string MarkdownLdGraphDocumentFactoryMissingMessage =
        "Markdown-LD custom document mode requires MarkdownLdGraphDocumentFactory to be configured.";
    private const string ToolNotInvokableMessageTemplate = "Tool '{0}' is not invokable.";
    private const string ToolIdOrToolNameRequiredMessage = "Either ToolId or ToolName is required.";
    private const string ToolIdNotFoundMessageTemplate = "Tool '{0}' was not found.";
    private const string ToolNameAmbiguousMessageTemplate =
        "Tool '{0}' is ambiguous. Use ToolId or specify SourceId explicitly.";
    private const string CatalogSourceMissingMessage =
        "ManagedCode.MCPGateway requires IMcpGatewayRegistry to be registered in the service provider. Use AddMcpGateway(...) to wire the package services.";
    private const string FailedToLoadGatewaySourceLogMessage =
        "Failed to load gateway source {SourceId}.";
    private const string GatewayGraphBuildFailedLogMessage =
        "Gateway Markdown-LD graph build failed.";
    private const string GatewayGraphSearchFailedLogMessage =
        "Gateway Markdown-LD graph search failed.";
    private const string EmbeddingGenerationFailedLogMessage =
        "Gateway embedding generation failed. Falling back to Markdown-LD graph search.";
    private const string GatewayIndexRebuiltLogMessage =
        "Gateway index rebuilt. Tools={ToolCount} VectorizedTools={VectorizedToolCount} GraphNodes={GraphNodeCount} GraphEdges={GraphEdgeCount}.";
    private const string GatewayVectorSearchFailedLogMessage =
        "Gateway vector search failed. Falling back to Markdown-LD graph ranking.";
    private const string GatewayInvocationFailedLogMessage =
        "Gateway invocation failed for {ToolId}.";
    private const string EmbeddingStoreLoadFailedLogMessage =
        "Loading stored tool embeddings failed. Falling back to generator-backed indexing.";
    private const string EmbeddingStoreSaveFailedLogMessage =
        "Persisting generated tool embeddings failed.";
    private const string GatewayQueryNormalizationFailedLogMessage =
        "Gateway search query normalization failed. Using original query.";
    private const string InputSchemaPropertiesPropertyName = "properties";
    private const string InputSchemaRequiredPropertyName = "required";
    private const string InputSchemaDescriptionPropertyName = "description";
    private const string InputSchemaTypePropertyName = "type";
    private const string InputSchemaEnumPropertyName = "enum";
    private const string DisplayNamePropertyName = "DisplayName";
    private const string SearchAliasesPropertyName = "SearchAliases";
    private const string SearchAliasesCamelCasePropertyName = "searchAliases";
    private const string SearchAliasesSnakeCasePropertyName = "search_aliases";
    private const string SearchAliasesShortPropertyName = "aliases";
    private const string SearchKeywordsPropertyName = "SearchKeywords";
    private const string SearchKeywordsCamelCasePropertyName = "searchKeywords";
    private const string SearchKeywordsSnakeCasePropertyName = "search_keywords";
    private const string SearchKeywordsShortPropertyName = "keywords";
    private const string CategoriesPropertyName = "Categories";
    private const string CategoriesCamelCasePropertyName = "categories";
    private const string CategoriesSnakeCasePropertyName = "categories";
    private const string CategoryPropertyName = "Category";
    private const string CategoryCamelCasePropertyName = "category";
    private const string CategorySnakeCasePropertyName = "category";
    private const string TagsPropertyName = "Tags";
    private const string TagsCamelCasePropertyName = "tags";
    private const string TagsSnakeCasePropertyName = "tags";
    private const string DataSourcesPropertyName = "DataSources";
    private const string DataSourcesCamelCasePropertyName = "dataSources";
    private const string DataSourcesSnakeCasePropertyName = "data_sources";
    private const string DataSourcePropertyName = "DataSource";
    private const string DataSourceCamelCasePropertyName = "dataSource";
    private const string DataSourceSnakeCasePropertyName = "data_source";
    private const string UsageExamplesPropertyName = "UsageExamples";
    private const string UsageExamplesCamelCasePropertyName = "usageExamples";
    private const string UsageExamplesSnakeCasePropertyName = "usage_examples";
    private const string ExamplesPropertyName = "Examples";
    private const string ExamplesCamelCasePropertyName = "examples";
    private const string ReadOnlyPropertyName = "ReadOnly";
    private const string ReadOnlyCamelCasePropertyName = "readOnly";
    private const string ReadOnlySnakeCasePropertyName = "read_only";
    private const string ReadOnlyHintPropertyName = "ReadOnlyHint";
    private const string ReadOnlyHintCamelCasePropertyName = "readOnlyHint";
    private const string ReadOnlyHintSnakeCasePropertyName = "read_only_hint";
    private const string IdempotentPropertyName = "Idempotent";
    private const string IdempotentCamelCasePropertyName = "idempotent";
    private const string IdempotentSnakeCasePropertyName = "idempotent";
    private const string IdempotentHintPropertyName = "IdempotentHint";
    private const string IdempotentHintCamelCasePropertyName = "idempotentHint";
    private const string IdempotentHintSnakeCasePropertyName = "idempotent_hint";
    private const string DestructivePropertyName = "Destructive";
    private const string DestructiveCamelCasePropertyName = "destructive";
    private const string DestructiveSnakeCasePropertyName = "destructive";
    private const string DestructiveHintPropertyName = "DestructiveHint";
    private const string DestructiveHintCamelCasePropertyName = "destructiveHint";
    private const string DestructiveHintSnakeCasePropertyName = "destructive_hint";
    private const string OpenWorldPropertyName = "OpenWorld";
    private const string OpenWorldCamelCasePropertyName = "openWorld";
    private const string OpenWorldSnakeCasePropertyName = "open_world";
    private const string OpenWorldHintPropertyName = "OpenWorldHint";
    private const string OpenWorldHintCamelCasePropertyName = "openWorldHint";
    private const string OpenWorldHintSnakeCasePropertyName = "open_world_hint";
    private const string CostTierPropertyName = "CostTier";
    private const string CostTierCamelCasePropertyName = "costTier";
    private const string CostTierSnakeCasePropertyName = "cost_tier";
    private const string LatencyTierPropertyName = "LatencyTier";
    private const string LatencyTierCamelCasePropertyName = "latencyTier";
    private const string LatencyTierSnakeCasePropertyName = "latency_tier";
    private const string EnabledByDefaultPropertyName = "EnabledByDefault";
    private const string EnabledByDefaultCamelCasePropertyName = "enabledByDefault";
    private const string EnabledByDefaultSnakeCasePropertyName = "enabled_by_default";
    private const string DefaultEnabledPropertyName = "DefaultEnabled";
    private const string DefaultEnabledCamelCasePropertyName = "defaultEnabled";
    private const string DefaultEnabledSnakeCasePropertyName = "default_enabled";
    private const string ToolNameLabel = "Tool name: ";
    private const string DisplayNameLabel = "Display name: ";
    private const string DescriptionLabel = "Description: ";
    private const string SearchAliasesLabel = "Search aliases: ";
    private const string SearchKeywordsLabel = "Search keywords: ";
    private const string CategoriesLabel = "Categories: ";
    private const string TagsLabel = "Tags: ";
    private const string DataSourcesLabel = "Data sources: ";
    private const string ReadOnlyLabel = "Read only: ";
    private const string IdempotentLabel = "Idempotent: ";
    private const string DestructiveLabel = "Destructive: ";
    private const string OpenWorldLabel = "Open world: ";
    private const string CostTierLabel = "Cost tier: ";
    private const string LatencyTierLabel = "Latency tier: ";
    private const string EnabledByDefaultLabel = "Enabled by default: ";
    private const string UsageExamplesHeading = "Usage examples:";
    private const string UsageExampleInputLabel = "Input";
    private const string UsageExampleOutputLabel = "Output";
    private const string UsageExampleDescriptionLabel = "Description";
    private const string RequiredArgumentsLabel = "Required arguments: ";
    private const string ParameterLabel = "Parameter ";
    private const string TypeLabel = "Type ";
    private const string TypicalValuesLabel = "Typical values: ";
    private const string InputSchemaLabel = "Input schema: ";
    private const string ContextSummaryPrefix = "context summary: ";
    private const string ContextPrefix = "context: ";
    private const string GraphKnowledgeBaseUriText =
        "https://managedcode.com/mcpgateway/knowledge/";
    private const string GraphLocalFederationEndpointUriText =
        "https://managedcode.com/mcpgateway/federation/local";
    private const string SchemaPrefixName = "schema";
    private const string KbPrefixName = "kb";
    private const string SkosPrefixName = "skos";
    private const string ProvPrefixName = "prov";
    private const string SchemaPrefixUri = "https://schema.org/";
    private const string KbPrefixUri = "urn:managedcode:markdown-ld-kb:vocab:";
    private const string SkosPrefixUri = "http://www.w3.org/2004/02/skos/core#";
    private const string ProvPrefixUri = "http://www.w3.org/ns/prov#";
    private const string SchemaTypeArticle = "schema:Article";
    private const string KbTypeMarkdownDocument = "kb:MarkdownDocument";
    private const string SchemaPredicateName = "schema:name";
    private const string SchemaPredicateDescription = "schema:description";
    private const string SchemaPredicateKeywords = "schema:keywords";
    private const string SchemaPredicateAbout = "schema:about";
    private const string SchemaPredicateMentions = "schema:mentions";
    private const string SchemaPredicateHasPart = "schema:hasPart";
    private const string KbPredicateRelatedTo = "kb:relatedTo";
    private const string KbPredicateNextStep = "kb:nextStep";
    private const string KbPredicateMemberOf = "kb:memberOf";
    private const string SkosPredicatePrefLabel = "skos:prefLabel";
    private const string ProvPredicateWasDerivedFrom = "prov:wasDerivedFrom";
    private const string GraphToolDocumentPathPrefix = "tools/";
    private const string GraphToolDocumentExtension = ".md";
    private const string GraphToolDocumentUriPrefix = "tools/";
    private const string GraphMarkdownFrontMatterDelimiter = "---";
    private const string GraphMarkdownHeadingPrefix = "# ";
    private const string GraphMarkdownToolIdentityHeading = "## Tool Identity";
    private const string GraphMarkdownExecutionContractHeading = "## Execution Contract";
    private const string GraphMarkdownInputSchemaHeading = "## Input Schema";
    private const string GraphMarkdownJsonFenceStart = "```json";
    private const string GraphMarkdownFenceEnd = "```";
    private const string GraphMarkdownListPrefix = "- ";
    private const string GraphYamlTitleKey = "title";
    private const string GraphYamlSummaryKey = "summary";
    private const string GraphYamlDescriptionKey = "description";
    private const string GraphYamlTagsKey = "tags";
    private const string GraphYamlGroupsKey = "graph_groups";
    private const string GraphYamlRelatedKey = "graph_related";
    private const string GraphYamlNextStepsKey = "graph_next_steps";
    private const string GraphTagGatewayTool = "mcp-gateway-tool";
    private const string GraphTagSourcePrefix = "source:";
    private const string GraphTagKindPrefix = "kind:";
    private const string GraphTagArgumentPrefix = "argument:";
    private const string GraphTagAliasPrefix = "alias:";
    private const string GraphTagKeywordPrefix = "keyword:";
    private const string GraphTagCategoryPrefix = "category:";
    private const string GraphTagMetadataPrefix = "tag:";
    private const string GraphTagDataSourcePrefix = "data-source:";
    private const string GraphTagReadOnlyPrefix = "read-only:";
    private const string GraphTagIdempotentPrefix = "idempotent:";
    private const string GraphTagDestructivePrefix = "destructive:";
    private const string GraphTagOpenWorldPrefix = "open-world:";
    private const string GraphTagCostTierPrefix = "cost-tier:";
    private const string GraphTagLatencyTierPrefix = "latency-tier:";
    private const string GraphTagEnabledPrefix = "enabled:";
    private const string GraphToolIdLabel = "Tool id: ";
    private const string GraphSourceIdLabel = "Source id: ";
    private const string GraphSourceKindLabel = "Source kind: ";
    private const string GraphGroupDomainPrefix = "domain:";
    private const string GraphGroupSourceDomainPrefix = "source-domain:";
    private const string GraphGroupCategoryPrefix = "category:";
    private const string GraphRelatedOperationDiscover = "discover";
    private const string GraphRelatedOperationInspect = "inspect";
    private const string GraphRelatedOperationAct = "act";
    private const string GraphRelatedOperationOther = "other";
    private const string GraphOperationTermSearch = "search";
    private const string GraphOperationTermFind = "find";
    private const string GraphOperationTermList = "list";
    private const string GraphOperationTermQuery = "query";
    private const string GraphOperationTermDiscover = "discover";
    private const string GraphOperationTermBrowse = "browse";
    private const string GraphOperationTermGet = "get";
    private const string GraphOperationTermRead = "read";
    private const string GraphOperationTermLookup = "lookup";
    private const string GraphOperationTermDetail = "detail";
    private const string GraphOperationTermDetails = "details";
    private const string GraphOperationTermFetch = "fetch";
    private const string GraphOperationTermShow = "show";
    private const string GraphOperationTermInspect = "inspect";
    private const string GraphOperationTermStatus = "status";
    private const string GraphOperationTermRetrieve = "retrieve";
    private const string GraphOperationTermCreate = "create";
    private const string GraphOperationTermUpdate = "update";
    private const string GraphOperationTermDelete = "delete";
    private const string GraphOperationTermRemove = "remove";
    private const string GraphOperationTermAdd = "add";
    private const string GraphOperationTermSet = "set";
    private const string GraphOperationTermSend = "send";
    private const string GraphOperationTermPost = "post";
    private const string GraphOperationTermWrite = "write";
    private const string GraphOperationTermInvoke = "invoke";
    private const string GraphOperationTermRun = "run";
    private const string GraphOperationTermExecute = "execute";
    private const string GraphGenericToolTerm = "tool";
    private const string GraphGenericToolsTerm = "tools";
    private const string GraphGenericMcpTerm = "mcp";
    private const string GraphGenericGatewayTerm = "gateway";
    private const string PluralSuffixIes = "ies";
    private const string PluralSuffixEs = "es";
    private const string FingerprintUnknownComponent = "unknown";
    private const string FingerprintComponentSeparator = "\n";
    private const string SearchQueryNormalizationInstructions =
        "Rewrite the user search request as a concise English tool-search query. Preserve identifiers, emails, repository names, CVE references, order numbers, tracking numbers, SKUs, version strings, filenames, and product names exactly. Do not answer the request. Do not explain anything. Return only the rewritten English search query. If the request is already concise English, return it unchanged.";
    private const int GraphMaxRelatedTokenSegments = 6;
    private const int GraphMaxRelatedToolsPerDocument = 4;
    private const int GraphMaxNextStepToolsPerDocument = 3;
    private const int GraphFocusedRelatedResultsLimit = 6;
    private const int GraphFocusedNextStepResultsLimit = 6;
    private const int GraphSchemaQueryMaxTerms = 14;
    private const int SearchQueryNormalizationMaxOutputTokens = 96;
    private const int GraphConfidenceMaxQueryTerms = 8;
    private const int GraphConfidenceTermWeightCap = 12;
    private const int GraphMinimumFuzzyTermLength = 4;
    private const int GraphRankedSearchMaxFuzzyEditDistance = 1;
    private const int GraphRankedBm25MaximumUnboundedCatalogSize = 32;
    private const int GraphSearchCandidateMultiplier = 1;
    private const int GraphSearchMinimumCandidateWindow = 5;
    private const int GraphSchemaCandidateMinimumWindow = 5;
    private const int FederatedSparqlQueryTimeoutMilliseconds = 15000;
    private const double SearchScoreMinimum = 0d;
    private const double SearchScoreMaximum = 1d;
    private const double GraphSchemaNameTextWeight = 1.2d;
    private const double GraphSchemaDescriptionTextWeight = 1.1d;
    private const double GraphSchemaKeywordsTextWeight = 0.95d;
    private const double GraphSchemaPrefLabelTextWeight = 0.75d;
    private const double GraphSchemaAboutRelationshipWeight = 0.9d;
    private const double GraphSchemaMentionsRelationshipWeight = 0.7d;
    private const double GraphSchemaGroupMembershipRelationshipWeight = 0.8d;
    private const double GraphSchemaProvenanceRelationshipWeight = 0.65d;
    private const double GraphSchemaRelatedExpansionScore = 0.75d;
    private const double GraphSchemaNextStepExpansionScore = 0.9d;
    private const double GraphSchemaMentionsExpansionScore = 0.35d;
    private const double GraphSchemaHasPartExpansionScore = 0.25d;
    private const double GraphCandidateIdfDocumentOffset = 1d;
    private const double GraphCandidateIdfFrequencyOffset = 0.5d;
    private const double GraphCandidateIdfBaseWeight = 1d;
    private const double GraphCandidateDefaultTermWeight = 1d;
    private const double GraphCandidateFuzzyScoreMultiplier = 0.6d;
    private const double GraphRankedSharedGroupRelatedScore = 0.7d;
    private const double GraphNavigationRelatedScore = 0.9d;
    private const double GraphNavigationNextStepScore = 0.8d;
    private const double ToolNameSignalWeight = 0.05d;
    private const double MinimumGraphMatchConfidence = 0.35d;
    private const double GraphConfidenceRawScoreWeight = 1d;
    private const double GraphConfidenceEvidenceWeight = 2d;
    private const double GraphConfidenceDiceCoefficientScale = 2d;
    private const double GraphContainsTermSimilarity = 0.92d;
    private const double GraphMinimumFuzzySimilarity = 0.55d;
    private const int AutoSupplementCandidateMultiplier = 4;
    private const int AutoSupplementMinimumCandidateWindow = 8;
    private const int AutoGraphSupplementMaximumUnboundedCatalogSize = 32;
    private const double ToolEmbeddingDefaultMagnitude = 0d;
    private const double EmbeddingCosineUnavailableScore = 0d;
    private const double EmbeddingDotProductInitialValue = 0d;
    private const double EmbeddingMagnitudeSquaredInitialValue = 0d;
    private const double RouteNoScoreAdjustment = 0d;
    private const double RouteEnabledByDefaultScoreBoost = 0.02d;
    private const double RouteReadOnlyPreferredScoreBoost = 0.08d;
    private const double RouteWritableWhenReadOnlyPreferredScorePenalty = -0.08d;
    private const double RouteWritablePreferredScoreBoost = 0.05d;
    private const double RouteReadOnlyWhenWritablePreferredScorePenalty = -0.03d;
    private const double RouteDestructiveWritableScoreBoost = 0.02d;
    private const double RouteNonDestructiveWritableScoreBoost = 0.01d;
    private const double RouteIdempotentScoreBoost = 0.01d;
    private const double RouteLowCostScoreBoost = 0.03d;
    private const double RouteMediumCostScoreBoost = 0.01d;
    private const double RouteHighCostScorePenalty = -0.02d;
    private const double RouteLowLatencyScoreBoost = 0.03d;
    private const double RouteMediumLatencyScoreBoost = 0.01d;
    private const double RouteHighLatencyScorePenalty = -0.02d;
    private const double RouteCategorizedToolScoreBoost = 0.01d;
    private const double RouteUsageExampleScoreBoost = 0.01d;

    private static readonly char[] TokenSeparators =
    [
        ' ',
        '\t',
        '\r',
        '\n',
        '_',
        '-',
        '.',
        ',',
        ';',
        ':',
        '/',
        '\\',
        '(',
        ')',
        '[',
        ']',
        '{',
        '}',
        '"',
        '\'',
        '@',
        '?',
        '!',
    ];
    private static readonly IReadOnlySet<string> IgnoredSearchTerms = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "a",
        "an",
        "and",
        "again",
        "any",
        "for",
        "just",
        "me",
        "need",
        "now",
        "please",
        "plz",
        "really",
        "something",
        "stuff",
        "that",
        "the",
        "thing",
        "this",
        "to",
        "active",
        "browser",
        "browsing",
        "context",
        "dashboard",
        "dashboards",
        "false",
        "filter",
        "filters",
        "intent",
        "mode",
        "page",
        "section",
        "signal",
        "signals",
        "summary",
        "true",
        "user",
        "with",
    };
    private static readonly IReadOnlySet<string> GraphDiscoveryTerms = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        GraphOperationTermSearch,
        GraphOperationTermFind,
        GraphOperationTermList,
        GraphOperationTermQuery,
        GraphOperationTermDiscover,
        GraphOperationTermBrowse,
    };
    private static readonly IReadOnlySet<string> GraphInspectionTerms = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        GraphOperationTermGet,
        GraphOperationTermRead,
        GraphOperationTermLookup,
        GraphOperationTermDetail,
        GraphOperationTermDetails,
        GraphOperationTermFetch,
        GraphOperationTermShow,
        GraphOperationTermInspect,
        GraphOperationTermStatus,
        GraphOperationTermRetrieve,
    };
    private static readonly IReadOnlySet<string> GraphActionTerms = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        GraphOperationTermCreate,
        GraphOperationTermUpdate,
        GraphOperationTermDelete,
        GraphOperationTermRemove,
        GraphOperationTermAdd,
        GraphOperationTermSet,
        GraphOperationTermSend,
        GraphOperationTermPost,
        GraphOperationTermWrite,
        GraphOperationTermInvoke,
        GraphOperationTermRun,
        GraphOperationTermExecute,
    };
    private static readonly IReadOnlySet<string> GraphGenericTerms = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        GraphGenericToolTerm,
        GraphGenericToolsTerm,
        GraphGenericMcpTerm,
        GraphGenericGatewayTerm,
    };
    private static readonly CompositeFormat SourceLoadFailedMessageFormat = CompositeFormat.Parse(
        SourceLoadFailedMessageTemplate
    );
    private static readonly CompositeFormat DuplicateToolIdMessageFormat = CompositeFormat.Parse(
        DuplicateToolIdMessageTemplate
    );
    private static readonly CompositeFormat GraphBuildFailedMessageFormat = CompositeFormat.Parse(
        GraphBuildFailedMessageTemplate
    );
    private static readonly CompositeFormat GraphSearchFailedMessageFormat = CompositeFormat.Parse(
        GraphSearchFailedMessageTemplate
    );
    private static readonly CompositeFormat GraphSchemaValidationMessageFormat =
        CompositeFormat.Parse(GraphSchemaValidationMessageTemplate);
    private static readonly CompositeFormat GraphFederationEndpointBlockedMessageFormat =
        CompositeFormat.Parse(GraphFederationEndpointBlockedMessageTemplate);
    private static readonly CompositeFormat GraphFederationEndpointInvalidMessageFormat =
        CompositeFormat.Parse(GraphFederationEndpointInvalidMessageTemplate);
    private static readonly CompositeFormat EmbeddingCountMismatchMessageFormat =
        CompositeFormat.Parse(EmbeddingCountMismatchMessageTemplate);
    private static readonly CompositeFormat QueryEmbeddingCountMismatchMessageFormat =
        CompositeFormat.Parse(QueryEmbeddingCountMismatchMessageTemplate);
    private static readonly CompositeFormat EmbeddingFailedMessageFormat = CompositeFormat.Parse(
        EmbeddingFailedMessageTemplate
    );
    private static readonly CompositeFormat EmbeddingStoreLoadFailedMessageFormat =
        CompositeFormat.Parse(EmbeddingStoreLoadFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingStoreSaveFailedMessageFormat =
        CompositeFormat.Parse(EmbeddingStoreSaveFailedMessageTemplate);
    private static readonly CompositeFormat QueryNormalizationFailedMessageFormat =
        CompositeFormat.Parse(QueryNormalizationFailedMessageTemplate);
    private static readonly CompositeFormat VectorSearchFailedMessageFormat = CompositeFormat.Parse(
        VectorSearchFailedMessageTemplate
    );
    private static readonly CompositeFormat ToolNotInvokableMessageFormat = CompositeFormat.Parse(
        ToolNotInvokableMessageTemplate
    );
    private static readonly CompositeFormat ToolIdNotFoundMessageFormat = CompositeFormat.Parse(
        ToolIdNotFoundMessageTemplate
    );
    private static readonly CompositeFormat ToolNameAmbiguousMessageFormat = CompositeFormat.Parse(
        ToolNameAmbiguousMessageTemplate
    );
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpGatewayRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpGatewayCatalogSource _catalogSource;
    private readonly McpGatewaySearchStrategy _searchStrategy;
    private readonly McpGatewayMarkdownLdGraphSearchMode _markdownLdGraphSearchMode;
    private readonly McpGatewayMarkdownLdGraphSource _markdownLdGraphSource;
    private readonly Func<
        IReadOnlyList<McpGatewayToolDescriptor>,
        CancellationToken,
        ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>
    >? _markdownLdGraphDocumentFactory;
    private readonly McpGatewaySearchQueryNormalization _searchQueryNormalization;
    private readonly string? _markdownLdGraphPath;
    private readonly int _defaultSearchLimit;
    private readonly int _maxSearchResults;
    private readonly int _maxDescriptorLength;
    private readonly IReadOnlyList<Uri> _markdownLdFederatedServiceEndpoints;
    private readonly Uri _graphLocalFederationEndpoint;
    private readonly IMcpGatewaySearchCache _searchRuntimeCache;
    private RuntimeState _state = RuntimeState.Empty;
    private BuildOperation? _buildOperation;
    private string? _embeddingGeneratorFingerprint;
    private string? _searchQueryChatClientFingerprint;

    internal McpGatewayRuntime(
        IServiceProvider serviceProvider,
        IOptions<McpGatewayOptions> options,
        ILogger<McpGatewayRuntime> logger,
        ILoggerFactory loggerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _serviceProvider = serviceProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;

        var resolvedOptions = options.Value;
        _catalogSource = ResolveCatalogSource(serviceProvider);
        _searchStrategy = resolvedOptions.SearchStrategy;
        _markdownLdGraphSearchMode = resolvedOptions.MarkdownLdGraphSearchMode;
        _markdownLdGraphSource = resolvedOptions.MarkdownLdGraphSource;
        _markdownLdGraphDocumentFactory = resolvedOptions.MarkdownLdGraphDocumentFactory;
        _searchQueryNormalization = resolvedOptions.SearchQueryNormalization;
        _markdownLdGraphPath = resolvedOptions.MarkdownLdGraphPath;
        _defaultSearchLimit = Math.Max(1, resolvedOptions.DefaultSearchLimit);
        _maxSearchResults = Math.Max(1, resolvedOptions.MaxSearchResults);
        _maxDescriptorLength = Math.Max(256, resolvedOptions.MaxDescriptorLength);
        _markdownLdFederatedServiceEndpoints = resolvedOptions.MarkdownLdFederatedServiceEndpoints;
        _graphLocalFederationEndpoint = new Uri(
            $"{GraphLocalFederationEndpointUriText}/{Guid.NewGuid():N}",
            UriKind.Absolute
        );
        _searchRuntimeCache = serviceProvider.GetRequiredService<IMcpGatewaySearchCache>();
    }

    public IReadOnlyList<AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
    ) => new McpGatewayToolSet(this).CreateTools(searchToolName, routeToolName, invokeToolName);

    public ValueTask DisposeAsync()
    {
        var previousState = Interlocked.Exchange(ref _state, RuntimeState.Disposed);
        if (previousState.IsDisposed)
        {
            return ValueTask.CompletedTask;
        }

        return ValueTask.CompletedTask;
    }

    private static IMcpGatewayCatalogSource ResolveCatalogSource(IServiceProvider serviceProvider)
    {
        if (
            serviceProvider.GetService<IMcpGatewayCatalogSource>()
            is IMcpGatewayCatalogSource catalogSource
        )
        {
            return catalogSource;
        }

        if (
            serviceProvider.GetService<IMcpGatewayRegistry>()
            is IMcpGatewayCatalogSource registryCatalogSource
        )
        {
            return registryCatalogSource;
        }

        throw new InvalidOperationException(CatalogSourceMissingMessage);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state).IsDisposed, this);
    }
}
