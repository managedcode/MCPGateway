#pragma warning disable MCPEXP001

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal enum McpGatewaySourceRegistrationKind
{
    Local,
    Http,
    Stdio,
    CustomMcpClient,
}

internal sealed record McpGatewayLoadedTool(
    AITool Tool,
    McpGatewayToolSearchHints? SearchHints = null,
    McpClient? Client = null
);

internal sealed record McpGatewayLoadedPrompt(Prompt ProtocolPrompt)
{
    public string Name => ProtocolPrompt.Name;
}

internal sealed record McpGatewayLoadedResource(Resource Resource);

internal sealed record McpGatewayLoadedResourceTemplate(ResourceTemplate ResourceTemplate);

internal abstract class McpGatewayToolSourceRegistration(string sourceId, string? displayName)
    : IAsyncDisposable
{
    public string SourceId { get; } = sourceId;

    public string? DisplayName { get; } = displayName;

    public abstract McpGatewaySourceRegistrationKind Kind { get; }

    public abstract ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    );

    public virtual async ValueTask<McpGatewayLoadedTool?> GetToolAsync(
        string toolName,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        var requestedToolName = toolName.Trim();
        var tools = await LoadToolsAsync(loggerFactory, cancellationToken);
        return tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Tool.Name, requestedToolName, StringComparison.Ordinal)
        );
    }

    public virtual ValueTask<IReadOnlyList<McpGatewayLoadedPrompt>> LoadPromptsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedPrompt>>([]);

    public virtual ValueTask<IReadOnlyList<McpGatewayLoadedResource>> LoadResourcesAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedResource>>([]);

    public virtual ValueTask<IReadOnlyList<McpGatewayLoadedResourceTemplate>> LoadResourceTemplatesAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedResourceTemplate>>([]);

    public virtual ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpGatewayPromptInvocationContext? promptContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<GetPromptResult?>(null);

    public virtual ValueTask<ReadResourceResult?> ReadResourceAsync(
        string resourceUri,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<ReadResourceResult?>(null);

    public virtual ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<CompleteResult?>(null);

    public virtual Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class McpGatewayHttpToolSourceRegistration(McpGatewayHttpServerOptions options)
    : McpGatewayClientToolSourceRegistration(
        options.SourceId,
        options.DisplayName,
        disposeClient: true
    )
{
    internal const HttpTransportMode CurrentTransportMode = HttpTransportMode.StreamableHttp;

    private readonly McpGatewayHttpServerOptions options = options;

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Http;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var transport = new HttpClientTransport(
            CreateTransportOptions(options),
            loggerFactory
        );

        try
        {
            return await McpClient.CreateAsync(
                transport,
                McpGatewayClientFactory.CreateClientOptions(),
                loggerFactory,
                cancellationToken
            );
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    internal static HttpClientTransportOptions CreateTransportOptions(McpGatewayHttpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = options.Endpoint,
            Name = options.SourceId,
            TransportMode = CurrentTransportMode,
            AdditionalHeaders = CreateAdditionalHeaders(options.AdditionalHeaders),
            OAuth = options.OAuth,
        };

        if (options.ConnectionTimeout is { } connectionTimeout)
        {
            transportOptions.ConnectionTimeout = connectionTimeout;
        }

        return transportOptions;
    }

    internal static HttpClientTransportOptions CreateTransportOptions(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers
    ) =>
        CreateTransportOptions(
            new McpGatewayHttpServerOptions
            {
                SourceId = sourceId,
                Endpoint = endpoint,
                AdditionalHeaders = headers,
            }
        );

    private static Dictionary<string, string>? CreateAdditionalHeaders(
        IReadOnlyDictionary<string, string>? headers
    )
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var additionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                additionalHeaders[key.Trim()] = value;
            }
        }

        return additionalHeaders.Count == 0 ? null : additionalHeaders;
    }
}

internal sealed class McpGatewayStdioToolSourceRegistration(McpGatewayStdioServerOptions options)
    : McpGatewayClientToolSourceRegistration(
        options.SourceId,
        options.DisplayName,
        disposeClient: true
    )
{
    private readonly McpGatewayStdioServerOptions options = options;

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Stdio;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var transport = new StdioClientTransport(CreateTransportOptions(options), loggerFactory);
        return await McpClient.CreateAsync(
            transport,
            McpGatewayClientFactory.CreateClientOptions(),
            loggerFactory,
            cancellationToken
        );
    }

    internal static StdioClientTransportOptions CreateTransportOptions(
        McpGatewayStdioServerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var transportOptions = new StdioClientTransportOptions
        {
            Name = options.SourceId,
            Command = options.Command,
            Arguments = options.Arguments?.ToList() ?? [],
            WorkingDirectory = options.WorkingDirectory,
            InheritEnvironmentVariables = options.InheritEnvironmentVariables,
            EnvironmentVariables = options.EnvironmentVariables is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(
                    options.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase
                ),
            StandardErrorLines = options.StandardErrorLines,
        };

        if (options.ShutdownTimeout is { } shutdownTimeout)
        {
            transportOptions.ShutdownTimeout = shutdownTimeout;
        }

        return transportOptions;
    }
}

internal sealed class McpGatewayProvidedClientToolSourceRegistration(
    string sourceId,
    Func<CancellationToken, ValueTask<McpClient>> clientFactory,
    bool disposeClient,
    string? displayName
) : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient)
{
    public override McpGatewaySourceRegistrationKind Kind =>
        McpGatewaySourceRegistrationKind.CustomMcpClient;

    protected override ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => clientFactory(cancellationToken);
}

internal abstract class McpGatewayClientToolSourceRegistration(
    string sourceId,
    string? displayName,
    bool disposeClient
) : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly McpGatewayMcpClientLifetime _clientLifetime = new(disposeClient);

    internal bool HasCachedClient => _clientLifetime.HasCachedClient;

    public override async ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        var tools = await client.ListToolsAsync(new RequestOptions(), cancellationToken);
        return tools
            .Cast<McpClientTool>()
            .Select(tool => new McpGatewayLoadedTool(tool, Client: client))
            .Cast<McpGatewayLoadedTool>()
            .ToList();
    }

    public override async ValueTask<IReadOnlyList<McpGatewayLoadedPrompt>> LoadPromptsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Prompts is null)
        {
            return [];
        }

        var prompts = await client.ListPromptsAsync(new RequestOptions(), cancellationToken);
        return prompts
            .Where(static prompt => !string.IsNullOrWhiteSpace(prompt.Name))
            .Select(static prompt => new McpGatewayLoadedPrompt(prompt.ProtocolPrompt))
            .ToList();
    }

    public override async ValueTask<IReadOnlyList<McpGatewayLoadedResource>> LoadResourcesAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Resources is null)
        {
            return [];
        }

        var resources = await client.ListResourcesAsync(new RequestOptions(), cancellationToken);
        return resources
            .Where(static resource => !string.IsNullOrWhiteSpace(resource.Uri))
            .Select(static resource => new McpGatewayLoadedResource(resource.ProtocolResource))
            .ToList();
    }

    public override async ValueTask<IReadOnlyList<McpGatewayLoadedResourceTemplate>> LoadResourceTemplatesAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Resources is null)
        {
            return [];
        }

        var templates = await client.ListResourceTemplatesAsync(
            new RequestOptions(),
            cancellationToken
        );
        return templates
            .Where(static template => !string.IsNullOrWhiteSpace(template.UriTemplate))
            .Select(static template => new McpGatewayLoadedResourceTemplate(
                template.ProtocolResourceTemplate
            ))
            .ToList();
    }

    public override async ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpGatewayPromptInvocationContext? promptContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Prompts is null)
        {
            return null;
        }

        return await client.GetPromptAsync(
            promptName,
            arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            new RequestOptions(),
            cancellationToken
        );
    }

    public override async ValueTask<ReadResourceResult?> ReadResourceAsync(
        string resourceUri,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Resources is null)
        {
            return null;
        }

        return await client.ReadResourceAsync(resourceUri, new RequestOptions(), cancellationToken);
    }

    public override async ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Completions is null)
        {
            return null;
        }

        return await client.CompleteAsync(
            new CompleteRequestParams
            {
                Ref = reference,
                Argument = argument,
                Context = context,
            },
            cancellationToken
        );
    }

    public override async Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Prompts?.ListChanged != true)
        {
            return null;
        }

        return await McpGatewayMcpClientSubscription.ListenForPromptListChangesAsync(
            client,
            onChanged,
            cancellationToken
        );
    }

    public override async Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Resources?.Subscribe != true)
        {
            return null;
        }

        return await McpGatewayMcpClientSubscription.ListenForResourceUpdatesAsync(
            client,
            resourceUri,
            onUpdated,
            cancellationToken
        );
    }

    protected abstract ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    );

    public override async ValueTask DisposeAsync()
    {
        await _clientLifetime.DisposeAsync();
        await base.DisposeAsync();
    }

    private async ValueTask<McpClient> GetClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await _clientLifetime.GetAsync(
            CreateClientAsync,
            loggerFactory,
            cancellationToken
        );
        if (
            !string.Equals(
                client.NegotiatedProtocolVersion,
                McpGatewayMcpProtocolConstants.CurrentProtocolVersion,
                StringComparison.Ordinal
            )
        )
        {
            throw new UnsupportedProtocolVersionException(
                client.NegotiatedProtocolVersion
                    ?? McpGatewayMcpProtocolConstants.MissingProtocolVersion,
                [McpGatewayMcpProtocolConstants.CurrentProtocolVersion]
            );
        }

        return client;
    }
}
