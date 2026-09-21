using System.Reflection;
using ManagedCode.MarkdownLd.Kb.Pipeline;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayOptions
{
    private const string MissingJsonLdResourceMessage = "JSON-LD graph resource was not found.";

    /// <summary>Default number of search matches returned when the caller does not request a size.</summary>
    public const int DefaultSearchLimitValue = 5;

    /// <summary>Default hard cap for caller-requested search and graph-search result sizes.</summary>
    public const int DefaultMaxSearchResults = 50;

    /// <summary>Default maximum descriptor document length used for search/index source text.</summary>
    public const int DefaultMaxDescriptorLength = 16 * 1024;

    /// <summary>Smallest valid search result limit.</summary>
    public const int MinimumSearchResultLimit = 1;

    /// <summary>Smallest valid descriptor document length.</summary>
    public const int MinimumDescriptorLength = 256;

    public static TimeSpan DefaultMarkdownLdFederatedSparqlQueryTimeout { get; } =
        TimeSpan.FromSeconds(30);

    private readonly McpGatewayRegistrationCollection _sourceRegistrations = new();
    private readonly List<Uri> _markdownLdFederatedServiceEndpoints = [];

    public McpGatewaySearchStrategy SearchStrategy { get; set; } = McpGatewaySearchStrategy.Graph;

    public McpGatewayMarkdownLdGraphSearchMode MarkdownLdGraphSearchMode { get; set; } =
        McpGatewayMarkdownLdGraphSearchMode.Hybrid;

    public McpGatewayMarkdownLdGraphSource MarkdownLdGraphSource { get; set; } =
        McpGatewayMarkdownLdGraphSource.GeneratedToolGraph;

    public string? MarkdownLdGraphPath { get; set; }

    internal Func<CancellationToken, ValueTask<string>>? JsonLdGraphLoader { get; private set; }

    internal Func<McpGatewayToolDescriptor, Uri>? JsonLdToolUriResolver { get; private set; }

    /// <summary>Optional schema search vocabulary for an application-owned graph.</summary>
    public KnowledgeGraphSchemaSearchProfile? MarkdownLdGraphSchemaSearchProfile { get; set; }

    public Func<
        IReadOnlyList<McpGatewayToolDescriptor>,
        CancellationToken,
        ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>
    >
        ? MarkdownLdGraphDocumentFactory
    { get; private set; }

    public McpGatewaySearchQueryNormalization SearchQueryNormalization { get; set; } =
        McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;

    public int DefaultSearchLimit { get; set; } = DefaultSearchLimitValue;

    public int MaxSearchResults { get; set; } = DefaultMaxSearchResults;

    public int MaxDescriptorLength { get; set; } = DefaultMaxDescriptorLength;

    public TimeSpan? MarkdownLdFederatedSparqlQueryTimeout { get; set; } =
        DefaultMarkdownLdFederatedSparqlQueryTimeout;

    public McpGatewayMcpTaskStoreOptions McpTaskStore { get; set; } = new();

    internal IReadOnlyList<McpGatewayToolSourceRegistration> SourceRegistrations =>
        _sourceRegistrations.Snapshot();

    internal IReadOnlyList<Uri> MarkdownLdFederatedServiceEndpoints =>
        _markdownLdFederatedServiceEndpoints.ToArray();

    public McpGatewayOptions AddTool(string sourceId, AITool tool, string? displayName = null) =>
        ConfigureRegistrations(registrations => registrations.AddTool(sourceId, tool, displayName));

    public McpGatewayOptions AddTool(
        string sourceId,
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddTool(sourceId, tool, searchHints, displayName)
        );

    public McpGatewayOptions AddTool(
        AITool tool,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations => registrations.AddTool(tool, sourceId, displayName));

    public McpGatewayOptions AddTool(
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddTool(tool, searchHints, sourceId, displayName)
        );

    public McpGatewayOptions AddTools(
        string sourceId,
        IEnumerable<AITool> tools,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddTools(sourceId, tools, displayName)
        );

    public McpGatewayOptions AddTools(
        IEnumerable<AITool> tools,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddTools(tools, sourceId, displayName)
        );

    public McpGatewayOptions AddPrompt(
        string sourceId,
        McpGatewayPrompt prompt,
        string? displayName = null
    ) => ConfigureRegistrations(registrations => registrations.AddPrompt(sourceId, prompt, displayName));

    public McpGatewayOptions AddPrompt(
        McpGatewayPrompt prompt,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) => ConfigureRegistrations(registrations => registrations.AddPrompt(prompt, sourceId, displayName));

    public McpGatewayOptions AddPrompts(
        string sourceId,
        IEnumerable<McpGatewayPrompt> prompts,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddPrompts(sourceId, prompts, displayName)
        );

    public McpGatewayOptions AddPrompts(
        IEnumerable<McpGatewayPrompt> prompts,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddPrompts(prompts, sourceId, displayName)
        );

    public McpGatewayOptions AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null
    ) =>
        AddHttpServer(
            new McpGatewayHttpServerOptions
            {
                SourceId = sourceId,
                Endpoint = endpoint,
                AdditionalHeaders = headers,
                DisplayName = displayName,
            }
        );

    public McpGatewayOptions AddHttpServer(McpGatewayHttpServerOptions httpServer) =>
        ConfigureRegistrations(registrations =>
            registrations.AddHttpServer(httpServer)
        );

    public McpGatewayOptions AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null
    ) =>
        AddStdioServer(
            new McpGatewayStdioServerOptions
            {
                SourceId = sourceId,
                Command = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = environmentVariables,
                DisplayName = displayName,
            }
        );

    public McpGatewayOptions AddStdioServer(McpGatewayStdioServerOptions stdioServer) =>
        ConfigureRegistrations(registrations =>
            registrations.AddStdioServer(stdioServer)
        );

    public McpGatewayOptions AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddMcpClient(sourceId, client, disposeClient, displayName)
        );

    public McpGatewayOptions AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null
    ) =>
        ConfigureRegistrations(registrations =>
            registrations.AddMcpClientFactory(sourceId, clientFactory, disposeClient, displayName)
        );

    public McpGatewayOptions UseGeneratedMarkdownLdGraph()
    {
        JsonLdGraphLoader = null;
        JsonLdToolUriResolver = null;
        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.GeneratedToolGraph;
        MarkdownLdGraphPath = null;
        MarkdownLdGraphDocumentFactory = null;
        return this;
    }

    public McpGatewayOptions UseHybridMarkdownLdGraphSearch()
    {
        MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.Hybrid;
        return this;
    }

    public McpGatewayOptions UseSchemaAwareMarkdownLdGraphSearch()
    {
        MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.SchemaAware;
        return this;
    }

    public McpGatewayOptions UseTokenDistanceMarkdownLdGraphSearch()
    {
        MarkdownLdGraphSearchMode = McpGatewayMarkdownLdGraphSearchMode.TokenDistance;
        return this;
    }

    public McpGatewayOptions AddMarkdownLdFederatedServiceEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "Markdown-LD federated service endpoints must be absolute URIs.",
                nameof(endpoint)
            );
        }

        _markdownLdFederatedServiceEndpoints.Add(endpoint);
        return this;
    }

    /// <summary>Load a JSON-LD graph from an embedded assembly resource without creating a filesystem copy.</summary>
    public McpGatewayOptions UseJsonLdGraphResource(Assembly assembly, string resourceName) =>
        UseJsonLdGraphResourceCore(assembly, resourceName, null);

    /// <summary>Load an owned JSON-LD resource and bind tools to its existing canonical node URIs.</summary>
    public McpGatewayOptions UseJsonLdGraphResource(
        Assembly assembly,
        string resourceName,
        Func<McpGatewayToolDescriptor, Uri> toolUriResolver)
    {
        ArgumentNullException.ThrowIfNull(toolUriResolver);
        return UseJsonLdGraphResourceCore(assembly, resourceName, toolUriResolver);
    }

    private McpGatewayOptions UseJsonLdGraphResourceCore(
        Assembly assembly,
        string resourceName,
        Func<McpGatewayToolDescriptor, Uri>? toolUriResolver)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.EmbeddedResource;
        MarkdownLdGraphPath = null;
        MarkdownLdGraphDocumentFactory = null;
        JsonLdToolUriResolver = toolUriResolver;
        JsonLdGraphLoader = async cancellationToken =>
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException(MissingJsonLdResourceMessage, resourceName);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        };
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphFile(string graphPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphPath);

        JsonLdGraphLoader = null;
        JsonLdToolUriResolver = null;
        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.FileSystem;
        MarkdownLdGraphPath = graphPath;
        MarkdownLdGraphDocumentFactory = null;
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(
        Func<
            IReadOnlyList<McpGatewayToolDescriptor>,
            CancellationToken,
            ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>
        > documentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(documentFactory);

        JsonLdGraphLoader = null;
        JsonLdToolUriResolver = null;
        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.CustomDocuments;
        MarkdownLdGraphPath = null;
        MarkdownLdGraphDocumentFactory = documentFactory;
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(
        IEnumerable<McpGatewayMarkdownLdGraphDocument> documents
    )
    {
        ArgumentNullException.ThrowIfNull(documents);

        var materializedDocuments = documents.ToArray();
        return UseMarkdownLdGraphDocuments(
            (descriptors, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>(
                    materializedDocuments
                );
            }
        );
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(
        Func<
            IReadOnlyList<McpGatewayToolDescriptor>,
            IReadOnlyList<McpGatewayMarkdownLdGraphDocument>
        > documentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(documentFactory);

        return UseMarkdownLdGraphDocuments(
            (descriptors, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(documentFactory(descriptors));
            }
        );
    }

    private McpGatewayOptions ConfigureRegistrations(
        Action<McpGatewayRegistrationCollection> configure
    )
    {
        configure(_sourceRegistrations);
        return this;
    }
}
