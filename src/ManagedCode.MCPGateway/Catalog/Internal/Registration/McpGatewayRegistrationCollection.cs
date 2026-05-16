using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayRegistrationCollection(
    IEnumerable<McpGatewayToolSourceRegistration>? registrations = null
)
{
    private const string CommandRequiredMessage = "A command is required.";
    private const string EndpointRequiredMessage = "An endpoint is required.";
    private const string SourceIdRequiredMessage = "A source id is required.";

    private ConcurrentQueue<McpGatewayToolSourceRegistration> _registrations = new(
        registrations ?? []
    );
    private ConcurrentDictionary<
        string,
        McpGatewayLocalToolSourceRegistration
    > _localRegistrations = CreateLocalRegistrations(registrations);

    public void AddTool(string sourceId, AITool tool, string? displayName = null) =>
        AddTool(tool, sourceId, displayName);

    public void AddTool(
        string sourceId,
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string? displayName = null
    ) => AddTool(tool, searchHints, sourceId, displayName);

    public void AddTool(
        AITool tool,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(tool);
        GetOrAddLocalRegistration(sourceId, displayName).AddTool(tool);
    }

    public void AddTool(
        AITool tool,
        McpGatewayToolSearchHints searchHints,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(searchHints);
        GetOrAddLocalRegistration(sourceId, displayName).AddTool(tool, searchHints);
    }

    public void AddTools(string sourceId, IEnumerable<AITool> tools, string? displayName = null) =>
        AddTools(tools, sourceId, displayName);

    public void AddTools(
        IEnumerable<AITool> tools,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(tools);

        var registration = GetOrAddLocalRegistration(sourceId, displayName);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            registration.AddTool(tool);
        }
    }

    public void AddPrompt(
        string sourceId,
        McpGatewayPrompt prompt,
        string? displayName = null
    ) => AddPrompt(prompt, sourceId, displayName);

    public void AddPrompt(
        McpGatewayPrompt prompt,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(prompt);
        GetOrAddLocalRegistration(sourceId, displayName).AddPrompt(prompt);
    }

    public void AddPrompts(
        string sourceId,
        IEnumerable<McpGatewayPrompt> prompts,
        string? displayName = null
    ) => AddPrompts(prompts, sourceId, displayName);

    public void AddPrompts(
        IEnumerable<McpGatewayPrompt> prompts,
        string sourceId = McpGatewayDefaults.DefaultSourceId,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(prompts);

        var registration = GetOrAddLocalRegistration(sourceId, displayName);
        foreach (var prompt in prompts)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            registration.AddPrompt(prompt);
        }
    }

    public void AddHttpServer(
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

    public void AddHttpServer(
        string sourceId,
        Uri endpoint,
        HttpTransportMode transportMode,
        IReadOnlyDictionary<string, string>? headers = null,
        string? displayName = null
    ) =>
        AddHttpServer(
            new McpGatewayHttpServerOptions
            {
                SourceId = sourceId,
                Endpoint = endpoint,
                TransportMode = transportMode,
                AdditionalHeaders = headers,
                DisplayName = displayName,
            }
        );

    public void AddHttpServer(McpGatewayHttpServerOptions httpServer)
    {
        ArgumentNullException.ThrowIfNull(httpServer);
        if (httpServer.Endpoint is null)
        {
            throw new ArgumentException(EndpointRequiredMessage, nameof(httpServer));
        }

        McpGatewayHttpToolSourceRegistration.ValidateTransportMode(httpServer.TransportMode);
        ValidateOptionalPositive(httpServer.ConnectionTimeout, nameof(httpServer.ConnectionTimeout));
        ValidateOptionalNonNegative(
            httpServer.MaxReconnectionAttempts,
            nameof(httpServer.MaxReconnectionAttempts)
        );
        ValidateOptionalPositive(
            httpServer.DefaultReconnectionInterval,
            nameof(httpServer.DefaultReconnectionInterval)
        );

        _registrations.Enqueue(
            new McpGatewayHttpToolSourceRegistration(
                httpServer.CloneWithSourceId(ValidateSourceId(httpServer.SourceId))
            )
        );
    }

    public void AddStdioServer(
        string sourceId,
        string command,
        IReadOnlyList<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        string? displayName = null
    )
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException(CommandRequiredMessage, nameof(command));
        }

        _registrations.Enqueue(
            new McpGatewayStdioToolSourceRegistration(
                ValidateSourceId(sourceId),
                command.Trim(),
                arguments,
                workingDirectory,
                environmentVariables,
                displayName
            )
        );
    }

    public void AddMcpClient(
        string sourceId,
        McpClient client,
        bool disposeClient = false,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        _registrations.Enqueue(
            new McpGatewayProvidedClientToolSourceRegistration(
                ValidateSourceId(sourceId),
                _ => ValueTask.FromResult(client),
                disposeClient,
                displayName
            )
        );
    }

    public void AddMcpClientFactory(
        string sourceId,
        Func<CancellationToken, ValueTask<McpClient>> clientFactory,
        bool disposeClient = true,
        string? displayName = null
    )
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _registrations.Enqueue(
            new McpGatewayProvidedClientToolSourceRegistration(
                ValidateSourceId(sourceId),
                clientFactory,
                disposeClient,
                displayName
            )
        );
    }

    public IReadOnlyList<McpGatewayToolSourceRegistration> Snapshot() => _registrations.ToArray();

    public IReadOnlyList<McpGatewayToolSourceRegistration> Drain()
    {
        Interlocked.Exchange(
            ref _localRegistrations,
            new ConcurrentDictionary<string, McpGatewayLocalToolSourceRegistration>(
                StringComparer.OrdinalIgnoreCase
            )
        );

        return Interlocked
            .Exchange(ref _registrations, new ConcurrentQueue<McpGatewayToolSourceRegistration>())
            .ToArray();
    }

    private McpGatewayLocalToolSourceRegistration GetOrAddLocalRegistration(
        string sourceId,
        string? displayName
    )
    {
        sourceId = ValidateSourceId(sourceId);

        McpGatewayLocalToolSourceRegistration? existing;
        while (!Volatile.Read(ref _localRegistrations).TryGetValue(sourceId, out existing))
        {
            var localRegistrations = Volatile.Read(ref _localRegistrations);
            var created = new McpGatewayLocalToolSourceRegistration(sourceId, displayName);
            if (localRegistrations.TryAdd(sourceId, created))
            {
                Volatile.Read(ref _registrations).Enqueue(created);
                return created;
            }
        }

        return existing;
    }

    private static string ValidateSourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException(SourceIdRequiredMessage, nameof(sourceId));
        }

        return sourceId.Trim();
    }

    private static void ValidateOptionalPositive(TimeSpan? value, string name)
    {
        if (value.HasValue && value.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive.");
        }
    }

    private static void ValidateOptionalNonNegative(int? value, string name)
    {
        if (value.HasValue && value.Value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be non-negative.");
        }
    }

    private static ConcurrentDictionary<
        string,
        McpGatewayLocalToolSourceRegistration
    > CreateLocalRegistrations(IEnumerable<McpGatewayToolSourceRegistration>? registrations)
    {
        var result = new ConcurrentDictionary<string, McpGatewayLocalToolSourceRegistration>(
            StringComparer.OrdinalIgnoreCase
        );
        if (registrations is null)
        {
            return result;
        }

        foreach (var registration in registrations.OfType<McpGatewayLocalToolSourceRegistration>())
        {
            result.TryAdd(registration.SourceId, registration);
        }

        return result;
    }
}
