using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayOptions
{
    private readonly List<McpGatewayToolSourceRegistration> _sourceRegistrations = [];

    public int DefaultSearchLimit { get; set; } = 8;

    public int MaxSearchResults { get; set; } = 20;

    public int MaxDescriptorLength { get; set; } = 4096;

    internal IReadOnlyList<McpGatewayToolSourceRegistration> SourceRegistrations => _sourceRegistrations;

    public McpGatewayOptions AddTool(string sourceId, AITool tool, string? displayName = null)
        => AddTool(tool, sourceId, displayName);

    public McpGatewayOptions AddTool(AITool tool, string sourceId = "local", string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        GetOrAddLocalRegistration(sourceId, displayName).AddTool(tool);
        return this;
    }

    public McpGatewayOptions AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null)
        => AddTools(tools, sourceId, displayName);

    public McpGatewayOptions AddTools(IEnumerable<AITool> tools, string sourceId = "local", string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var registration = GetOrAddLocalRegistration(sourceId, displayName);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            registration.AddTool(tool);
        }

        return this;
    }

    public McpGatewayOptions AddHttpServer(
        string sourceId,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        _sourceRegistrations.Add(new McpGatewayHttpToolSourceRegistration(
            ValidateSourceId(sourceId),
            endpoint,
            headers,
            displayName));
        return this;
    }

    public McpGatewayOptions AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A command is required.", nameof(command));
        }

        _sourceRegistrations.Add(new McpGatewayStdioToolSourceRegistration(
            ValidateSourceId(sourceId),
            command.Trim(),
            arguments,
            workingDirectory,
            environmentVariables,
            displayName));
        return this;
    }

    public McpGatewayOptions AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _sourceRegistrations.Add(new McpGatewayProvidedClientToolSourceRegistration(
            ValidateSourceId(sourceId),
            _ => ValueTask.FromResult(client),
            disposeClient,
            displayName));
        return this;
    }

    public McpGatewayOptions AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);

        _sourceRegistrations.Add(new McpGatewayProvidedClientToolSourceRegistration(
            ValidateSourceId(sourceId),
            clientFactory,
            disposeClient,
            displayName));
        return this;
    }

    private McpGatewayLocalToolSourceRegistration GetOrAddLocalRegistration(string sourceId, string? displayName)
    {
        sourceId = ValidateSourceId(sourceId);

        var existing = _sourceRegistrations
            .OfType<McpGatewayLocalToolSourceRegistration>()
            .FirstOrDefault(item => string.Equals(item.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new McpGatewayLocalToolSourceRegistration(sourceId, displayName);
        _sourceRegistrations.Add(created);
        return created;
    }

    private static string ValidateSourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(sourceId));
        }

        return sourceId.Trim();
    }
}

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
    private readonly List<AITool> _tools = [];

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

    public void AddTool(AITool tool) => _tools.Add(tool);

    public override ValueTask<IReadOnlyList<AITool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<AITool>>(_tools.ToList());
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
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly bool _disposeClient = disposeClient;
    private McpClient? _client;
    private Task<McpClient>? _clientTask;

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
        if (_disposeClient && _client is not null)
        {
            await _client.DisposeAsync();
        }

        _sync.Dispose();
        await base.DisposeAsync();
    }

    private async Task<McpClient> GetClientAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        if (_clientTask is not null)
        {
            _client = await _clientTask.WaitAsync(cancellationToken);
            return _client;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            _clientTask ??= CreateClientAsync(loggerFactory, CancellationToken.None).AsTask();
        }
        finally
        {
            _sync.Release();
        }

        _client = await _clientTask.WaitAsync(cancellationToken);
        return _client;
    }
}

internal static class McpGatewayClientFactory
{
    public static McpClientOptions CreateClientOptions()
        => new()
        {
            ClientInfo = new Implementation
            {
                Name = "managedcode-mcpgateway",
                Version = "1.0.0"
            }
        };
}
