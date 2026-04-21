using System.Collections.Concurrent;
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
    McpGatewayToolSearchHints? SearchHints = null
);

internal sealed record McpGatewayLoadedPrompt(
    string Name,
    string? Title,
    string? Description,
    IReadOnlyList<PromptArgument> Arguments
);

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

    public virtual ValueTask<IReadOnlyList<McpGatewayLoadedPrompt>> LoadPromptsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedPrompt>>([]);

    public virtual ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<GetPromptResult?>(null);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class McpGatewayLocalToolSourceRegistration(string sourceId, string? displayName)
    : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly ConcurrentQueue<McpGatewayLoadedTool> _tools = new();

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

    public void AddTool(AITool tool, McpGatewayToolSearchHints? searchHints = null) =>
        _tools.Enqueue(new McpGatewayLoadedTool(tool, searchHints));

    public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>(_tools.ToArray());
}

internal sealed class McpGatewayHttpToolSourceRegistration(
    string sourceId,
    Uri endpoint,
    IReadOnlyDictionary<string, string>? headers,
    string? displayName
) : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient: true)
{
    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Http;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var httpClient = new HttpClient();
        if (headers is { Count: > 0 })
        {
            foreach (var (key, value) in headers)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
                }
            }
        }

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint, Name = SourceId },
            httpClient,
            loggerFactory,
            ownsHttpClient: true
        );

        return await McpClient.CreateAsync(
            transport,
            McpGatewayClientFactory.CreateClientOptions(),
            loggerFactory,
            cancellationToken
        );
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
        return tools.Cast<AITool>().Select(static tool => new McpGatewayLoadedTool(tool)).ToList();
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

    public override async ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
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

        if (_disposeClient && Volatile.Read(ref _client) is { } client)
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
        try
        {
            clientSource.SetResult(
                await CreateClientAsync(loggerFactory, clientOperation.CancellationToken)
            );
        }
        catch (OperationCanceledException)
            when (clientOperation.CancellationToken.IsCancellationRequested)
        {
            clientSource.SetCanceled(clientOperation.CancellationToken);
        }
        catch (Exception ex)
        {
            clientSource.SetException(ex);
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

            var cachedClient = Volatile.Read(ref _client);
            return cachedClient ?? Interlocked.CompareExchange(ref _client, client, null) ?? client;
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
        { }
    }

    private sealed record ClientOperation(
        Task<McpClient> Task,
        CancellationToken CancellationToken
    );
}
