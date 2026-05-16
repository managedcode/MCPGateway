#pragma warning disable MCPEXP001

using System.Collections.Concurrent;
using System.Text.Json;
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
    ToolTaskSupport? TaskSupport = null
);

internal sealed record McpGatewayLoadedPrompt(
    string Name,
    string? Title,
    string? Description,
    IReadOnlyList<PromptArgument> Arguments
);

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

    public virtual Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
        Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<JsonElement?>(null);

    public virtual ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<McpTask?>(null);

    public virtual Task<IAsyncDisposable?> SubscribeToResourceAsync(
        string resourceUri,
        Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => Task.FromResult<IAsyncDisposable?>(null);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class McpGatewayLocalToolSourceRegistration(string sourceId, string? displayName)
    : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly ConcurrentQueue<McpGatewayLoadedTool> _tools = new();
    private readonly ConcurrentDictionary<string, McpGatewayPrompt> _prompts = new(
        StringComparer.Ordinal
    );

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

    public void AddTool(AITool tool, McpGatewayToolSearchHints? searchHints = null) =>
        _tools.Enqueue(
            new McpGatewayLoadedTool(
                tool,
                searchHints,
                McpGatewayToolTaskSupportResolver.Resolve(tool)
            )
        );

    public void AddPrompt(McpGatewayPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!_prompts.TryAdd(prompt.Name, prompt))
        {
            throw new InvalidOperationException(
                $"Prompt '{prompt.Name}' is already registered for source '{SourceId}'."
            );
        }
    }

    public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>(_tools.ToArray());

    public override ValueTask<IReadOnlyList<McpGatewayLoadedPrompt>> LoadPromptsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedPrompt>>(
            _prompts
                .Values.OrderBy(static prompt => prompt.Name, StringComparer.Ordinal)
                .Select(static prompt => new McpGatewayLoadedPrompt(
                    prompt.Name,
                    prompt.DisplayName,
                    prompt.Description,
                    prompt
                        .Arguments.Select(static argument => new PromptArgument
                        {
                            Name = argument.Name,
                            Title = argument.DisplayName,
                            Description = argument.Description,
                            Required = argument.IsRequired,
                        })
                        .ToList()
                ))
                .ToList()
        );

    public override async ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpGatewayPromptInvocationContext? promptContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(promptName))
        {
            return null;
        }

        if (!_prompts.TryGetValue(promptName.Trim(), out var prompt))
        {
            return null;
        }

        var renderContext = new McpGatewayPromptRenderContext(
            SourceId,
            prompt.Name,
            arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            promptContext?.Services ?? EmptyServiceProvider.Instance,
            (request, token) =>
                promptContext?.RenderPromptAsync(request, token)
                ?? ValueTask.FromResult<GetPromptResult?>(null)
        );
        var result = await prompt.RenderAsync(renderContext, cancellationToken);
        ArgumentNullException.ThrowIfNull(result);

        result.Description = string.IsNullOrWhiteSpace(result.Description)
            ? prompt.Description
            : result.Description;
        return result;
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
        if (
            reference is not PromptReference promptReference
            || string.IsNullOrWhiteSpace(promptReference.Name)
        )
        {
            return null;
        }

        if (!_prompts.TryGetValue(promptReference.Name.Trim(), out var prompt))
        {
            return null;
        }

        if (prompt.CompleteAsync is null || string.IsNullOrWhiteSpace(argument.Name))
        {
            return null;
        }

        return await prompt.CompleteAsync(
            new McpGatewayPromptCompletionContext(
                SourceId,
                prompt.Name,
                argument.Name.Trim(),
                argument.Value ?? string.Empty,
                context,
                serviceProvider ?? EmptyServiceProvider.Instance
            ),
            cancellationToken
        );
    }
}

internal sealed class McpGatewayHttpToolSourceRegistration(McpGatewayHttpServerOptions options)
    : McpGatewayClientToolSourceRegistration(
        options.SourceId,
        options.DisplayName,
        disposeClient: true
    )
{
    internal const HttpTransportMode DefaultTransportMode = HttpTransportMode.StreamableHttp;

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
            TransportMode = ValidateTransportMode(options.TransportMode),
            AdditionalHeaders = CreateAdditionalHeaders(options.AdditionalHeaders),
            OAuth = options.OAuth,
        };

        if (options.ConnectionTimeout is { } connectionTimeout)
        {
            transportOptions.ConnectionTimeout = connectionTimeout;
        }

        if (options.KnownSessionId is { } knownSessionId)
        {
            transportOptions.KnownSessionId = knownSessionId;
        }

        if (options.OwnsSession is { } ownsSession)
        {
            transportOptions.OwnsSession = ownsSession;
        }

        if (options.MaxReconnectionAttempts is { } maxReconnectionAttempts)
        {
            transportOptions.MaxReconnectionAttempts = maxReconnectionAttempts;
        }

        if (options.DefaultReconnectionInterval is { } defaultReconnectionInterval)
        {
            transportOptions.DefaultReconnectionInterval = defaultReconnectionInterval;
        }

        return transportOptions;
    }

    internal static HttpClientTransportOptions CreateTransportOptions(
        string sourceId,
        Uri endpoint,
        HttpTransportMode transportMode,
        IReadOnlyDictionary<string, string>? headers
    ) =>
        CreateTransportOptions(
            new McpGatewayHttpServerOptions
            {
                SourceId = sourceId,
                Endpoint = endpoint,
                TransportMode = transportMode,
                AdditionalHeaders = headers,
            }
        );

    internal static HttpTransportMode ValidateTransportMode(HttpTransportMode transportMode)
    {
        if (!Enum.IsDefined(transportMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportMode),
                transportMode,
                "HTTP MCP transport mode is not supported."
            );
        }

        return transportMode;
    }

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

internal sealed class McpGatewayStdioToolSourceRegistration(
    string sourceId,
    string command,
    IReadOnlyList<string>? arguments,
    string? workingDirectory,
    IReadOnlyDictionary<string, string?>? environmentVariables,
    string? displayName
) : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient: true)
{
    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Stdio;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var options = new StdioClientTransportOptions
        {
            Name = SourceId,
            Command = command,
            Arguments = arguments?.ToList() ?? [],
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(
                    environmentVariables,
                    StringComparer.OrdinalIgnoreCase
                ),
        };

        var transport = new StdioClientTransport(options, loggerFactory);
        return await McpClient.CreateAsync(
            transport,
            McpGatewayClientFactory.CreateClientOptions(),
            loggerFactory,
            cancellationToken
        );
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
    private readonly bool _disposeClient = disposeClient;
    private McpClient? _client;
    private ClientOperation? _clientOperation;
    private int _disposed;

    public override async ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        var tools = await client.ListToolsAsync(new RequestOptions(), cancellationToken);
        return tools
            .Cast<McpClientTool>()
            .Select(static tool =>
                new McpGatewayLoadedTool(
                    tool,
                    TaskSupport: McpGatewayToolTaskSupportResolver.Resolve(tool)
                )
            )
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
            .Select(static prompt => new McpGatewayLoadedPrompt(
                Name: prompt.Name.Trim(),
                Title: prompt.Title,
                Description: prompt.Description,
                Arguments: prompt.ProtocolPrompt.Arguments?.ToList() ?? []
            ))
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

    public override async Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
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

        cancellationToken.ThrowIfCancellationRequested();
        return client.RegisterNotificationHandler(
            NotificationMethods.PromptListChangedNotification,
            (notification, token) =>
            {
                var payload = notification.Params?.Deserialize<PromptListChangedNotificationParams>()
                    ?? new PromptListChangedNotificationParams();
                return onChanged(payload, token);
            }
        );
    }

    public override async ValueTask<McpTask?> CallToolAsTaskAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpTaskMetadata taskMetadata,
        IProgress<ProgressNotificationValue>? progress,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Tasks?.Requests?.Tools?.Call is null)
        {
            return null;
        }

        return await client.CallToolAsTaskAsync(
            toolName,
            arguments,
            taskMetadata,
            progress,
            new RequestOptions(),
            cancellationToken
        );
    }

    public override async ValueTask<McpTask?> GetTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Tasks is null)
        {
            return null;
        }

        return await client.GetTaskAsync(taskId, new RequestOptions(), cancellationToken);
    }

    public override async ValueTask<JsonElement?> GetTaskResultAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Tasks is null)
        {
            return null;
        }

        return await client.GetTaskResultAsync(taskId, new RequestOptions(), cancellationToken);
    }

    public override async ValueTask<McpTask?> CancelTaskAsync(
        string taskId,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Tasks?.Cancel is null)
        {
            return null;
        }

        return await client.CancelTaskAsync(taskId, new RequestOptions(), cancellationToken);
    }

    public override async Task<IAsyncDisposable?> SubscribeToResourceAsync(
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

        return await client.SubscribeToResourceAsync(
            resourceUri,
            onUpdated,
            new RequestOptions(),
            cancellationToken
        );
    }

    public override async Task<IAsyncDisposable?> SubscribeToTaskStatusAsync(
        string taskId,
        Func<McpTaskStatusNotificationParams, CancellationToken, ValueTask> onUpdated,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        if (client.ServerCapabilities.Tasks is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var registration = client.RegisterNotificationHandler(
            NotificationMethods.TaskStatusNotification,
            (notification, token) =>
            {
                var payload = notification.Params?.Deserialize<McpTaskStatusNotificationParams>();
                if (
                    payload is null
                    || !string.Equals(payload.TaskId, taskId, StringComparison.Ordinal)
                )
                {
                    return ValueTask.CompletedTask;
                }

                return onUpdated(payload, token);
            }
        );

        return registration;
    }

    protected abstract ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    );

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_disposeClient && Interlocked.Exchange(ref _client, null) is { } client)
        {
            await client.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private async Task<McpClient> GetClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Volatile.Read(ref _client) is { } client)
        {
            return client;
        }

        var clientTask = Volatile.Read(ref _clientOperation);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (clientTask is null)
            {
                var clientSource = new TaskCompletionSource<McpClient>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var createdTask = new ClientOperation(clientSource.Task, cancellationToken);
                if (Interlocked.CompareExchange(ref _clientOperation, createdTask, null) is null)
                {
                    _ = RunCreateClientAsync(clientSource, loggerFactory, createdTask);
                    clientTask = createdTask;
                    break;
                }

                clientTask = Volatile.Read(ref _clientOperation);
                continue;
            }

            if (clientTask.CancellationToken.IsCancellationRequested)
            {
                await AwaitCanceledClientCreationAsync(clientTask);
                _ = Interlocked.CompareExchange(ref _clientOperation, null, clientTask);
                clientTask = Volatile.Read(ref _clientOperation);
                continue;
            }

            if (clientTask.Task.IsCanceled || clientTask.Task.IsFaulted)
            {
                _ = Interlocked.CompareExchange(ref _clientOperation, null, clientTask);
                clientTask = Volatile.Read(ref _clientOperation);
                continue;
            }

            break;
        }

        if (clientTask is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await AwaitClientTaskAsync(clientTask!.Task, cancellationToken);
    }

    private async Task RunCreateClientAsync(
        TaskCompletionSource<McpClient> clientSource,
        ILoggerFactory loggerFactory,
        ClientOperation clientOperation
    )
    {
        McpClient? createdClient = null;
        try
        {
            createdClient = await CreateClientAsync(loggerFactory, clientOperation.CancellationToken);
            var cachedClient = await CacheCreatedClientAsync(createdClient);
            createdClient = null;
            clientSource.SetResult(cachedClient);
        }
        catch (OperationCanceledException)
            when (clientOperation.CancellationToken.IsCancellationRequested)
        {
            clientSource.SetCanceled(clientOperation.CancellationToken);
        }
        catch (Exception ex)
        {
            var exception = ex;
            if (createdClient is not null && _disposeClient)
            {
                try
                {
                    await createdClient.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    exception = new AggregateException(ex, cleanupException);
                }
            }

            clientSource.SetException(exception);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _clientOperation, null, clientOperation);
        }
    }

    private async Task<McpClient> AwaitClientTaskAsync(
        Task<McpClient> clientTask,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var client = await clientTask.WaitAsync(cancellationToken);
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_disposeClient)
                {
                    await client.DisposeAsync();
                }

                throw new ObjectDisposedException(GetType().Name);
            }

            return client;
        }
        catch when (clientTask.IsFaulted || clientTask.IsCanceled)
        {
            if (
                Volatile.Read(ref _clientOperation) is { Task: { } currentTask } currentOperation
                && ReferenceEquals(currentTask, clientTask)
            )
            {
                _ = Interlocked.CompareExchange(ref _clientOperation, null, currentOperation);
            }
            throw;
        }
    }

    private static async Task AwaitCanceledClientCreationAsync(ClientOperation clientOperation)
    {
        try
        {
            await clientOperation.Task;
        }
        catch (OperationCanceledException)
            when (clientOperation.CancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async ValueTask<McpClient> CacheCreatedClientAsync(McpClient client)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var cachedClient = Volatile.Read(ref _client);
        if (cachedClient is null)
        {
            cachedClient = Interlocked.CompareExchange(ref _client, client, null);
            if (cachedClient is null)
            {
                return client;
            }
        }

        if (!ReferenceEquals(cachedClient, client) && _disposeClient)
        {
            await client.DisposeAsync();
        }

        return cachedClient;
    }

    private sealed record ClientOperation(
        Task<McpClient> Task,
        CancellationToken CancellationToken
    );
}

#pragma warning disable MCPEXP001

internal static class McpGatewayToolTaskSupportResolver
{
    public static ToolTaskSupport Resolve(AITool tool) =>
        tool is McpClientTool clientTool
            ? clientTool.ProtocolTool.Execution?.TaskSupport ?? ToolTaskSupport.Forbidden
            : ToolTaskSupport.Optional;
}

#pragma warning restore MCPEXP001
