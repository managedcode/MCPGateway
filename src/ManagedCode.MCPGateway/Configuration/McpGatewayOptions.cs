using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayOptions
{
    private readonly McpGatewayRegistrationCollection _sourceRegistrations = new();

    public McpGatewaySearchStrategy SearchStrategy { get; set; } = McpGatewaySearchStrategy.Graph;

    public McpGatewayMarkdownLdGraphSource MarkdownLdGraphSource { get; set; } =
        McpGatewayMarkdownLdGraphSource.GeneratedToolGraph;

    public string? MarkdownLdGraphPath { get; set; }

    public Func<IReadOnlyList<McpGatewayToolDescriptor>, CancellationToken, ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>>?
        MarkdownLdGraphDocumentFactory
    { get; private set; }

    public McpGatewaySearchQueryNormalization SearchQueryNormalization { get; set; } =
        McpGatewaySearchQueryNormalization.TranslateToEnglishWhenAvailable;

    public int DefaultSearchLimit { get; set; } = 5;

    public int MaxSearchResults { get; set; } = 15;

    public int MaxDescriptorLength { get; set; } = 4096;

    internal IReadOnlyList<McpGatewayToolSourceRegistration> SourceRegistrations => _sourceRegistrations.Snapshot();

    public McpGatewayOptions AddTool(string sourceId, AITool tool, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTool(sourceId, tool, displayName));

    public McpGatewayOptions AddTool(string sourceId, AITool tool, McpGatewayToolSearchHints searchHints, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTool(sourceId, tool, searchHints, displayName));

    public McpGatewayOptions AddTool(AITool tool, string sourceId = McpGatewayDefaults.DefaultSourceId, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTool(tool, sourceId, displayName));

    public McpGatewayOptions AddTool(AITool tool, McpGatewayToolSearchHints searchHints, string sourceId = McpGatewayDefaults.DefaultSourceId, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTool(tool, searchHints, sourceId, displayName));

    public McpGatewayOptions AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTools(sourceId, tools, displayName));

    public McpGatewayOptions AddTools(IEnumerable<AITool> tools, string sourceId = McpGatewayDefaults.DefaultSourceId, string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddTools(tools, sourceId, displayName));

    public McpGatewayOptions AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddHttpServer(sourceId, endpoint, headers, displayName));

    public McpGatewayOptions AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddStdioServer(sourceId, command, arguments, workingDirectory, environmentVariables, displayName));

    public McpGatewayOptions AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddMcpClient(sourceId, client, disposeClient, displayName));

    public McpGatewayOptions AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null)
        => ConfigureRegistrations(registrations => registrations.AddMcpClientFactory(sourceId, clientFactory, disposeClient, displayName));

    public McpGatewayOptions UseGeneratedMarkdownLdGraph()
    {
        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.GeneratedToolGraph;
        MarkdownLdGraphPath = null;
        MarkdownLdGraphDocumentFactory = null;
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphFile(string graphPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphPath);

        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.FileSystem;
        MarkdownLdGraphPath = graphPath;
        MarkdownLdGraphDocumentFactory = null;
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(
        Func<IReadOnlyList<McpGatewayToolDescriptor>, CancellationToken, ValueTask<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>> documentFactory)
    {
        ArgumentNullException.ThrowIfNull(documentFactory);

        MarkdownLdGraphSource = McpGatewayMarkdownLdGraphSource.CustomDocuments;
        MarkdownLdGraphPath = null;
        MarkdownLdGraphDocumentFactory = documentFactory;
        return this;
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(IEnumerable<McpGatewayMarkdownLdGraphDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var materializedDocuments = documents.ToArray();
        return UseMarkdownLdGraphDocuments((descriptors, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<McpGatewayMarkdownLdGraphDocument>>(materializedDocuments);
        });
    }

    public McpGatewayOptions UseMarkdownLdGraphDocuments(
        Func<IReadOnlyList<McpGatewayToolDescriptor>, IReadOnlyList<McpGatewayMarkdownLdGraphDocument>> documentFactory)
    {
        ArgumentNullException.ThrowIfNull(documentFactory);

        return UseMarkdownLdGraphDocuments((descriptors, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(documentFactory(descriptors));
        });
    }

    private McpGatewayOptions ConfigureRegistrations(Action<McpGatewayRegistrationCollection> configure)
    {
        configure(_sourceRegistrations);
        return this;
    }
}
