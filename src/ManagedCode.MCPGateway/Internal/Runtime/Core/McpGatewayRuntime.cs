using System.Text;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime : IMcpGateway
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
    private const string EmbeddingCountMismatchDiagnosticCode = "embedding_count_mismatch";
    private const string EmbeddingGeneratorMissingDiagnosticCode = "embedding_generator_missing";
    private const string EmbeddingFailedDiagnosticCode = "embedding_failed";
    private const string EmbeddingStoreLoadFailedDiagnosticCode = "embedding_store_load_failed";
    private const string EmbeddingStoreSaveFailedDiagnosticCode = "embedding_store_save_failed";
    private const string QueryVectorEmptyDiagnosticCode = "query_vector_empty";
    private const string QueryNormalizedDiagnosticCode = "query_normalized";
    private const string QueryNormalizationFailedDiagnosticCode = "query_normalization_failed";
    private const string VectorSearchFailedDiagnosticCode = "vector_search_failed";
    private const string MarkdownLdGraphPathMissingDiagnosticCode = "markdown_ld_graph_path_missing";
    private const string MarkdownLdGraphDocumentFactoryMissingDiagnosticCode = "markdown_ld_graph_document_factory_missing";
    private const string SourceLoadFailedMessageTemplate = "Failed to load tools from source '{0}': {1}";
    private const string DuplicateToolIdMessageTemplate = "Skipped duplicate tool id '{0}'.";
    private const string GraphBuildFailedMessageTemplate = "Building the Markdown-LD tool graph failed: {0}";
    private const string GraphFallbackMessage = "Vector search was unavailable or unusable. Markdown-LD graph ranking was used.";
    private const string LowConfidenceResultsMessage = "Graph ranking confidence was low for this query.";
    private const string HybridVectorMergeMessage = "Vector-first ranking was supplemented with Markdown-LD graph expansion.";
    private const string GraphUnavailableMessage = "Markdown-LD graph ranking is unavailable.";
    private const string GraphSearchFailedMessageTemplate = "Markdown-LD graph ranking failed: {0}";
    private const string EmbeddingCountMismatchMessageTemplate = "Embedding generation returned {0} vectors for {1} tools.";
    private const string EmbeddingGeneratorMissingMessage = "No keyed or unkeyed IEmbeddingGenerator<string, Embedding<float>> is registered. Stored tool embeddings may be reused, but search falls back to Markdown-LD graph ranking without a query embedding generator.";
    private const string EmbeddingFailedMessageTemplate = "Embedding generation failed: {0}";
    private const string EmbeddingStoreLoadFailedMessageTemplate = "Loading stored tool embeddings failed: {0}";
    private const string EmbeddingStoreSaveFailedMessageTemplate = "Persisting generated tool embeddings failed: {0}";
    private const string QueryVectorEmptyMessage = "Embedding generator returned an empty query vector.";
    private const string QueryNormalizedMessage = "Search query was normalized to English before ranking.";
    private const string QueryNormalizationFailedMessageTemplate = "Search query normalization failed and the original query was used: {0}";
    private const string VectorSearchFailedMessageTemplate = "Vector ranking failed and Markdown-LD graph fallback was used: {0}";
    private const string MarkdownLdGraphPathMissingMessage = "Markdown-LD graph file mode requires MarkdownLdGraphPath to point to a graph bundle file, Markdown source file, or directory.";
    private const string MarkdownLdGraphDocumentFactoryMissingMessage = "Markdown-LD custom document mode requires MarkdownLdGraphDocumentFactory to be configured.";
    private const string ToolNotInvokableMessageTemplate = "Tool '{0}' is not invokable.";
    private const string ToolIdOrToolNameRequiredMessage = "Either ToolId or ToolName is required.";
    private const string ToolIdNotFoundMessageTemplate = "Tool '{0}' was not found.";
    private const string ToolNameAmbiguousMessageTemplate = "Tool '{0}' is ambiguous. Use ToolId or specify SourceId explicitly.";
    private const string CatalogSourceMissingMessage = "ManagedCode.MCPGateway requires IMcpGatewayRegistry to be registered in the service provider. Use AddMcpGateway(...) to wire the package services.";
    private const string FailedToLoadGatewaySourceLogMessage = "Failed to load gateway source {SourceId}.";
    private const string GatewayGraphBuildFailedLogMessage = "Gateway Markdown-LD graph build failed.";
    private const string GatewayGraphSearchFailedLogMessage = "Gateway Markdown-LD graph search failed.";
    private const string EmbeddingGenerationFailedLogMessage = "Gateway embedding generation failed. Falling back to Markdown-LD graph search.";
    private const string GatewayIndexRebuiltLogMessage = "Gateway index rebuilt. Tools={ToolCount} VectorizedTools={VectorizedToolCount} GraphNodes={GraphNodeCount} GraphEdges={GraphEdgeCount}.";
    private const string GatewayVectorSearchFailedLogMessage = "Gateway vector search failed. Falling back to Markdown-LD graph ranking.";
    private const string GatewayInvocationFailedLogMessage = "Gateway invocation failed for {ToolId}.";
    private const string EmbeddingStoreLoadFailedLogMessage = "Loading stored tool embeddings failed. Falling back to generator-backed indexing.";
    private const string EmbeddingStoreSaveFailedLogMessage = "Persisting generated tool embeddings failed.";
    private const string GatewayQueryNormalizationFailedLogMessage = "Gateway search query normalization failed. Using original query.";
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
    private const string ToolNameLabel = "Tool name: ";
    private const string DisplayNameLabel = "Display name: ";
    private const string DescriptionLabel = "Description: ";
    private const string SearchAliasesLabel = "Search aliases: ";
    private const string SearchKeywordsLabel = "Search keywords: ";
    private const string RequiredArgumentsLabel = "Required arguments: ";
    private const string ParameterLabel = "Parameter ";
    private const string TypeLabel = "Type ";
    private const string TypicalValuesLabel = "Typical values: ";
    private const string InputSchemaLabel = "Input schema: ";
    private const string ContextSummaryPrefix = "context summary: ";
    private const string ContextPrefix = "context: ";
    private const string GraphKnowledgeBaseUriText = "https://managedcode.com/mcpgateway/knowledge/";
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
    private const string GraphToolIdLabel = "Tool id: ";
    private const string GraphSourceIdLabel = "Source id: ";
    private const string GraphSourceKindLabel = "Source kind: ";
    private const string GraphGroupDomainPrefix = "domain:";
    private const string GraphGroupSourceDomainPrefix = "source-domain:";
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
    private const string SearchQueryNormalizationInstructions = "Rewrite the user search request as a concise English tool-search query. Preserve identifiers, emails, repository names, CVE references, order numbers, tracking numbers, SKUs, version strings, filenames, and product names exactly. Do not answer the request. Do not explain anything. Return only the rewritten English search query. If the request is already concise English, return it unchanged.";
    private const int GraphMaxRelatedTokenSegments = 6;
    private const int GraphMaxRelatedToolsPerDocument = 4;
    private const int GraphMaxNextStepToolsPerDocument = 3;
    private const int GraphFocusedRelatedResultsLimit = 6;
    private const int GraphFocusedNextStepResultsLimit = 6;
    private const int SearchQueryNormalizationMaxOutputTokens = 96;
    private const int GraphConfidenceMaxQueryTerms = 8;
    private const int GraphConfidenceTermWeightCap = 12;
    private const int GraphMinimumFuzzyTermLength = 4;
    private const double ToolNameSignalWeight = 0.05d;
    private const double MinimumGraphMatchConfidence = 0.35d;
    private const double GraphConfidenceEvidenceWeight = 2d;
    private const double GraphContainsTermSimilarity = 0.92d;
    private const double GraphMinimumFuzzySimilarity = 0.55d;
    private const int AutoSupplementCandidateMultiplier = 4;
    private const int AutoSupplementMinimumCandidateWindow = 8;

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
        '!'
    ];
    private static readonly IReadOnlySet<string> IgnoredSearchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        "with"
    };
    private static readonly IReadOnlySet<string> GraphDiscoveryTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GraphOperationTermSearch,
        GraphOperationTermFind,
        GraphOperationTermList,
        GraphOperationTermQuery,
        GraphOperationTermDiscover,
        GraphOperationTermBrowse
    };
    private static readonly IReadOnlySet<string> GraphInspectionTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        GraphOperationTermRetrieve
    };
    private static readonly IReadOnlySet<string> GraphActionTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
        GraphOperationTermExecute
    };
    private static readonly IReadOnlySet<string> GraphGenericTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        GraphGenericToolTerm,
        GraphGenericToolsTerm,
        GraphGenericMcpTerm,
        GraphGenericGatewayTerm
    };
    private static readonly CompositeFormat SourceLoadFailedMessageFormat = CompositeFormat.Parse(SourceLoadFailedMessageTemplate);
    private static readonly CompositeFormat DuplicateToolIdMessageFormat = CompositeFormat.Parse(DuplicateToolIdMessageTemplate);
    private static readonly CompositeFormat GraphBuildFailedMessageFormat = CompositeFormat.Parse(GraphBuildFailedMessageTemplate);
    private static readonly CompositeFormat GraphSearchFailedMessageFormat = CompositeFormat.Parse(GraphSearchFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingCountMismatchMessageFormat = CompositeFormat.Parse(EmbeddingCountMismatchMessageTemplate);
    private static readonly CompositeFormat EmbeddingFailedMessageFormat = CompositeFormat.Parse(EmbeddingFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingStoreLoadFailedMessageFormat = CompositeFormat.Parse(EmbeddingStoreLoadFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingStoreSaveFailedMessageFormat = CompositeFormat.Parse(EmbeddingStoreSaveFailedMessageTemplate);
    private static readonly CompositeFormat QueryNormalizationFailedMessageFormat = CompositeFormat.Parse(QueryNormalizationFailedMessageTemplate);
    private static readonly CompositeFormat VectorSearchFailedMessageFormat = CompositeFormat.Parse(VectorSearchFailedMessageTemplate);
    private static readonly CompositeFormat ToolNotInvokableMessageFormat = CompositeFormat.Parse(ToolNotInvokableMessageTemplate);
    private static readonly CompositeFormat ToolIdNotFoundMessageFormat = CompositeFormat.Parse(ToolIdNotFoundMessageTemplate);
    private static readonly CompositeFormat ToolNameAmbiguousMessageFormat = CompositeFormat.Parse(ToolNameAmbiguousMessageTemplate);
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpGatewayRuntime> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpGatewayCatalogSource _catalogSource;
    private readonly McpGatewaySearchStrategy _searchStrategy;
    private readonly McpGatewayMarkdownLdGraphSource _markdownLdGraphSource;
    private readonly Func<IReadOnlyList<McpGatewayToolDescriptor>, CancellationToken, ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>>?
        _markdownLdGraphDocumentFactory;
    private readonly McpGatewaySearchQueryNormalization _searchQueryNormalization;
    private readonly string? _markdownLdGraphPath;
    private readonly int _defaultSearchLimit;
    private readonly int _maxSearchResults;
    private readonly int _maxDescriptorLength;
    private readonly IMcpGatewaySearchCache _searchRuntimeCache;
    private RuntimeState _state = RuntimeState.Empty;
    private BuildOperation? _buildOperation;
    private string? _embeddingGeneratorFingerprint;
    private string? _searchQueryChatClientFingerprint;

    internal McpGatewayRuntime(
        IServiceProvider serviceProvider,
        IOptions<McpGatewayOptions> options,
        ILogger<McpGatewayRuntime> logger,
        ILoggerFactory loggerFactory)
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
        _markdownLdGraphSource = resolvedOptions.MarkdownLdGraphSource;
        _markdownLdGraphDocumentFactory = resolvedOptions.MarkdownLdGraphDocumentFactory;
        _searchQueryNormalization = resolvedOptions.SearchQueryNormalization;
        _markdownLdGraphPath = resolvedOptions.MarkdownLdGraphPath;
        _defaultSearchLimit = Math.Max(1, resolvedOptions.DefaultSearchLimit);
        _maxSearchResults = Math.Max(1, resolvedOptions.MaxSearchResults);
        _maxDescriptorLength = Math.Max(256, resolvedOptions.MaxDescriptorLength);
        _searchRuntimeCache = serviceProvider.GetRequiredService<IMcpGatewaySearchCache>();
    }

    public IReadOnlyList<AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
        => new McpGatewayToolSet(this).CreateTools(searchToolName, invokeToolName);

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
        if (serviceProvider.GetService<IMcpGatewayCatalogSource>() is IMcpGatewayCatalogSource catalogSource)
        {
            return catalogSource;
        }

        if (serviceProvider.GetService<IMcpGatewayRegistry>() is IMcpGatewayCatalogSource registryCatalogSource)
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
