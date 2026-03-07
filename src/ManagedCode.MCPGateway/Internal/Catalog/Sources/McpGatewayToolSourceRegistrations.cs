using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal enum McpGatewaySourceRegistrationKind
{
    Local,
    Http,
    Stdio,
    CustomMcpClient
}

internal abstract class McpGatewayToolSourceRegistration(string sourceId, string? displayName)
    : IAsyncDisposable
{
    public string SourceId { get; } = sourceId;

    public string? DisplayName { get; } = displayName;

    public abstract McpGatewaySourceRegistrationKind Kind { get; }

    public abstract ValueTask<IReadOnlyList<AITool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class McpGatewayLocalToolSourceRegistration(string sourceId, string? displayName)
    : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly ConcurrentQueue<AITool> _tools = new();

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

    public void AddTool(AITool tool) => _tools.Enqueue(tool);

    public override ValueTask<IReadOnlyList<AITool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<AITool>>(_tools.ToArray());
}

internal sealed class McpGatewayHttpToolSourceRegistration(
    string sourceId,
    Uri endpoint,
    IReadOnlyDictionary<string, string>? headers,
    string? displayName)
    : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient: true)
{
    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Http;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
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
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = SourceId
            },
            httpClient,
            loggerFactory,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            McpGatewayClientFactory.CreateClientOptions(),
            loggerFactory,
            cancellationToken);
    }
}

internal sealed class McpGatewayStdioToolSourceRegistration(
    string sourceId,
    string command,
    IReadOnlyList<string>? arguments,
    string? workingDirectory,
    IReadOnlyDictionary<string, string?>? environmentVariables,
    string? displayName)
    : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient: true)
{
    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Stdio;

    protected override async ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var options = new StdioClientTransportOptions
        {
            Name = SourceId,
            Command = command,
            Arguments = arguments?.ToList() ?? [],
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(environmentVariables, StringComparer.OrdinalIgnoreCase)
        };

        var transport = new StdioClientTransport(options, loggerFactory);
        return await McpClient.CreateAsync(
            transport,
            McpGatewayClientFactory.CreateClientOptions(),
            loggerFactory,
            cancellationToken);
    }
}

internal sealed class McpGatewayProvidedClientToolSourceRegistration(
    string sourceId,
    Func<CancellationToken, ValueTask<McpClient>> clientFactory,
    bool disposeClient,
    string? displayName)
    : McpGatewayClientToolSourceRegistration(sourceId, displayName, disposeClient)
{
    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.CustomMcpClient;

    protected override ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => clientFactory(cancellationToken);
}

internal abstract class McpGatewayClientToolSourceRegistration(
    string sourceId,
    string? displayName,
    bool disposeClient)
    : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly bool _disposeClient = disposeClient;
    private McpClient? _client;
    private Task<McpClient>? _clientTask;
    private int _disposed;

    public override async ValueTask<IReadOnlyList<AITool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(loggerFactory, cancellationToken);
        var tools = await client.ListToolsAsync(new RequestOptions(), cancellationToken);
        return tools.Cast<AITool>().ToList();
    }

    protected abstract ValueTask<McpClient> CreateClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Volatile.Read(ref _client) is { } client)
        {
            return client;
        }

        while (true)
        {
            var existingTask = Volatile.Read(ref _clientTask);
            if (existingTask is not null)
            {
                return await AwaitClientTaskAsync(existingTask, cancellationToken);
            }

            var createdTask = CreateClientAsync(loggerFactory, CancellationToken.None).AsTask();
            if (Interlocked.CompareExchange(ref _clientTask, createdTask, null) is null)
            {
                return await AwaitClientTaskAsync(createdTask, cancellationToken);
            }
        }
    }

    private async Task<McpClient> AwaitClientTaskAsync(
        Task<McpClient> clientTask,
        CancellationToken cancellationToken)
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
            _ = Interlocked.CompareExchange(ref _clientTask, null, clientTask);
            throw;
        }
    }
}
