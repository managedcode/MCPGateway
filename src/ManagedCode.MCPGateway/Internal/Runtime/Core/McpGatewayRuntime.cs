using System.Text;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime : IMcpGateway
{
    private const string QueryArgumentName = "query";
    private const string ContextArgumentName = "context";
    private const string ContextSummaryArgumentName = "contextSummary";
    private const string GatewayInvocationMetaKey = "managedCodeMcpGateway";
    private const string SearchModeEmpty = "empty";
    private const string SearchModeBrowse = "browse";
    private const string SearchModeLexical = "lexical";
    private const string SearchModeVector = "vector";
    private const string SourceLoadFailedDiagnosticCode = "source_load_failed";
    private const string DuplicateToolIdDiagnosticCode = "duplicate_tool_id";
    private const string EmbeddingCountMismatchDiagnosticCode = "embedding_count_mismatch";
    private const string EmbeddingGeneratorMissingDiagnosticCode = "embedding_generator_missing";
    private const string EmbeddingFailedDiagnosticCode = "embedding_failed";
    private const string EmbeddingStoreLoadFailedDiagnosticCode = "embedding_store_load_failed";
    private const string EmbeddingStoreSaveFailedDiagnosticCode = "embedding_store_save_failed";
    private const string QueryVectorEmptyDiagnosticCode = "query_vector_empty";
    private const string LexicalFallbackDiagnosticCode = "lexical_fallback";
    private const string VectorSearchFailedDiagnosticCode = "vector_search_failed";
    private const string SourceLoadFailedMessageTemplate = "Failed to load tools from source '{0}': {1}";
    private const string DuplicateToolIdMessageTemplate = "Skipped duplicate tool id '{0}'.";
    private const string EmbeddingCountMismatchMessageTemplate = "Embedding generation returned {0} vectors for {1} tools.";
    private const string EmbeddingGeneratorMissingMessage = "No keyed or unkeyed IEmbeddingGenerator<string, Embedding<float>> is registered. Stored tool embeddings may be reused, but search falls back lexically without a query embedding generator.";
    private const string EmbeddingFailedMessageTemplate = "Embedding generation failed: {0}";
    private const string EmbeddingStoreLoadFailedMessageTemplate = "Loading stored tool embeddings failed: {0}";
    private const string EmbeddingStoreSaveFailedMessageTemplate = "Persisting generated tool embeddings failed: {0}";
    private const string QueryVectorEmptyMessage = "Embedding generator returned an empty query vector.";
    private const string LexicalFallbackMessage = "Vector search is unavailable. Lexical ranking was used.";
    private const string VectorSearchFailedMessageTemplate = "Vector ranking failed and lexical fallback was used: {0}";
    private const string ToolNotInvokableMessageTemplate = "Tool '{0}' is not invokable.";
    private const string ToolIdOrToolNameRequiredMessage = "Either ToolId or ToolName is required.";
    private const string ToolIdNotFoundMessageTemplate = "Tool '{0}' was not found.";
    private const string ToolNameAmbiguousMessageTemplate = "Tool '{0}' is ambiguous. Use ToolId or specify SourceId explicitly.";
    private const string FailedToLoadGatewaySourceLogMessage = "Failed to load gateway source {SourceId}.";
    private const string EmbeddingGenerationFailedLogMessage = "Gateway embedding generation failed. Falling back to lexical search.";
    private const string GatewayIndexRebuiltLogMessage = "Gateway index rebuilt. Tools={ToolCount} VectorizedTools={VectorizedToolCount}.";
    private const string GatewayVectorSearchFailedLogMessage = "Gateway vector search failed. Falling back to lexical ranking.";
    private const string GatewayInvocationFailedLogMessage = "Gateway invocation failed for {ToolId}.";
    private const string EmbeddingStoreLoadFailedLogMessage = "Loading stored tool embeddings failed. Falling back to generator-backed indexing.";
    private const string EmbeddingStoreSaveFailedLogMessage = "Persisting generated tool embeddings failed.";
    private const string InputSchemaPropertiesPropertyName = "properties";
    private const string InputSchemaRequiredPropertyName = "required";
    private const string InputSchemaDescriptionPropertyName = "description";
    private const string InputSchemaTypePropertyName = "type";
    private const string InputSchemaEnumPropertyName = "enum";
    private const string DisplayNamePropertyName = "DisplayName";
    private const string ToolNameLabel = "Tool name: ";
    private const string DisplayNameLabel = "Display name: ";
    private const string DescriptionLabel = "Description: ";
    private const string RequiredArgumentsLabel = "Required arguments: ";
    private const string ParameterLabel = "Parameter ";
    private const string TypeLabel = "Type ";
    private const string TypicalValuesLabel = "Typical values: ";
    private const string InputSchemaLabel = "Input schema: ";
    private const string ContextSummaryPrefix = "context summary: ";
    private const string ContextPrefix = "context: ";
    private const string PluralSuffixIes = "ies";
    private const string PluralSuffixEs = "es";
    private const string CharacterNGramPrefix = "tri:";
    private const string EmbeddingGeneratorFingerprintUnknownComponent = "unknown";
    private const string EmbeddingGeneratorFingerprintComponentSeparator = "\n";
    private const int CharacterNGramLength = 3;
    private const double ToolNameTokenWeight = 5d;
    private const double DisplayNameTokenWeight = 4d;
    private const double DescriptionTokenWeight = 3d;
    private const double RequiredArgumentTokenWeight = 2.25d;
    private const double ParameterNameTokenWeight = 2.5d;
    private const double ParameterDescriptionTokenWeight = 2d;
    private const double ParameterTypeTokenWeight = 0.75d;
    private const double EnumValuesTokenWeight = 1.5d;
    private const double HumanizedIdentifierWeightFactor = 0.85d;
    private const double CharacterNGramTokenWeightFactor = 0.35d;
    private const double QueryTokenWeight = 3d;
    private const double ContextSummaryTokenWeight = 1.5d;
    private const double ContextTokenWeight = 1d;
    private const double TokenSimilarityWeight = 0.55d;
    private const double TokenCoverageWeight = 0.15d;
    private const double LexicalSimilarityWeight = 0.1d;
    private const double ApproximateTermSimilarityWeight = 0.2d;

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
    private static readonly CompositeFormat SourceLoadFailedMessageFormat = CompositeFormat.Parse(SourceLoadFailedMessageTemplate);
    private static readonly CompositeFormat DuplicateToolIdMessageFormat = CompositeFormat.Parse(DuplicateToolIdMessageTemplate);
    private static readonly CompositeFormat EmbeddingCountMismatchMessageFormat = CompositeFormat.Parse(EmbeddingCountMismatchMessageTemplate);
    private static readonly CompositeFormat EmbeddingFailedMessageFormat = CompositeFormat.Parse(EmbeddingFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingStoreLoadFailedMessageFormat = CompositeFormat.Parse(EmbeddingStoreLoadFailedMessageTemplate);
    private static readonly CompositeFormat EmbeddingStoreSaveFailedMessageFormat = CompositeFormat.Parse(EmbeddingStoreSaveFailedMessageTemplate);
    private static readonly CompositeFormat VectorSearchFailedMessageFormat = CompositeFormat.Parse(VectorSearchFailedMessageTemplate);
    private static readonly CompositeFormat ToolNotInvokableMessageFormat = CompositeFormat.Parse(ToolNotInvokableMessageTemplate);
    private static readonly CompositeFormat ToolIdNotFoundMessageFormat = CompositeFormat.Parse(ToolIdNotFoundMessageTemplate);
    private static readonly CompositeFormat ToolNameAmbiguousMessageFormat = CompositeFormat.Parse(ToolNameAmbiguousMessageTemplate);
    private static readonly IReadOnlyDictionary<string, double> EmptyTokenWeights =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpGateway> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMcpGatewayCatalogSource _catalogSource;
    private readonly IAsyncDisposable? _ownedCatalogSource;
    private readonly McpGatewaySearchStrategy _searchStrategy;
    private readonly Tokenizer _searchTokenizer;
    private readonly int _defaultSearchLimit;
    private readonly int _maxSearchResults;
    private readonly int _maxDescriptorLength;
    private RuntimeState _state = RuntimeState.Empty;
    private Task<McpGatewayIndexBuildResult>? _buildTask;

    internal McpGatewayRuntime(
        IServiceProvider serviceProvider,
        IOptions<McpGatewayOptions> options,
        ILogger<McpGateway> logger,
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
        var catalogSource = serviceProvider.GetService<IMcpGatewayCatalogSource>();
        if (catalogSource is null)
        {
            var ownedCatalogSource = new McpGatewayRegistry(options);
            _catalogSource = ownedCatalogSource;
            _ownedCatalogSource = ownedCatalogSource;
        }
        else
        {
            _catalogSource = catalogSource;
        }
        _searchStrategy = resolvedOptions.SearchStrategy;
        _searchTokenizer = McpGatewayTokenSearchTokenizerFactory.GetTokenizer(resolvedOptions.TokenSearchTokenizer);
        _defaultSearchLimit = Math.Max(1, resolvedOptions.DefaultSearchLimit);
        _maxSearchResults = Math.Max(1, resolvedOptions.MaxSearchResults);
        _maxDescriptorLength = Math.Max(256, resolvedOptions.MaxDescriptorLength);
    }

    public IReadOnlyList<AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName)
        => new McpGatewayToolSet(this).CreateTools(searchToolName, invokeToolName);

    public async ValueTask DisposeAsync()
    {
        var previousState = Interlocked.Exchange(ref _state, RuntimeState.Disposed);
        if (previousState.IsDisposed)
        {
            return;
        }

        if (_ownedCatalogSource is not null)
        {
            await _ownedCatalogSource.DisposeAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state).IsDisposed, this);
    }
}
