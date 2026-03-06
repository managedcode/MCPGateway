using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestMcpServerHost(
    ServiceProvider serviceProvider,
    McpClient client,
    ModelContextProtocol.Server.McpServer server,
    CancellationTokenSource cancellationTokenSource,
    Task serverTask,
    IReadOnlyList<JsonObject> capturedMeta) : IAsyncDisposable
{
    public McpClient Client { get; } = client;

    public IReadOnlyList<JsonObject> CapturedMeta { get; } = capturedMeta;

    public static async Task<TestMcpServerHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var capturedMeta = new List<JsonObject>();
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddMcpServer()
            .WithToolsFromAssembly(typeof(TestMcpTools).Assembly);

        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var options = serviceProvider.GetRequiredService<IOptions<McpServerOptions>>();

        options.Value.Filters.Request.CallToolFilters.Add(next => async (request, token) =>
        {
            if (request.Params?.Meta is JsonObject meta)
            {
                capturedMeta.Add((JsonObject)meta.DeepClone());
            }
            else if (request.Params?.Meta is not null &&
                     JsonSerializer.SerializeToNode(request.Params.Meta) is JsonObject serializedMeta)
            {
                capturedMeta.Add(serializedMeta);
            }

            return await next(request, token);
        });

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var serverTransport = new StreamServerTransport(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream());

        var server = ModelContextProtocol.Server.McpServer.Create(
            serverTransport,
            options.Value,
            loggerFactory,
            serviceProvider);

        var serverCancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(serverCancellation.Token);

        var clientTransport = new StreamClientTransport(
            clientToServer.Writer.AsStream(),
            serverToClient.Reader.AsStream(),
            loggerFactory);

        var client = await McpClient.CreateAsync(
            clientTransport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "managedcode-mcpgateway-tests",
                    Version = "1.0.0"
                }
            },
            loggerFactory,
            cancellationToken);

        return new TestMcpServerHost(
            serviceProvider,
            client,
            server,
            serverCancellation,
            serverTask,
            capturedMeta);
    }

    public async ValueTask DisposeAsync()
    {
        cancellationTokenSource.Cancel();

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        await Client.DisposeAsync();
        await server.DisposeAsync();
        cancellationTokenSource.Dispose();
        await serviceProvider.DisposeAsync();
    }

    [McpServerToolType]
    private sealed class TestMcpTools
    {
        [McpServerTool(
            Name = "github_repository_search",
            Title = "Search GitHub repositories",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = true)]
        [Description("Search GitHub repositories by query text.")]
        public static TestMcpSearchResult SearchRepositories(
            [Description("Repository search query.")] string query)
            => new(query, "mcp");

        [McpServerTool(
            Name = "json_text_search",
            Title = "Return JSON as text",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = false)]
        [Description("Return a JSON document as plain text content.")]
        public static string ReturnJsonAsText(
            [Description("Payload query text.")] string query)
            => JsonSerializer.Serialize(new TestMcpSearchResult(query, "text-json"));

        [McpServerTool(
            Name = "plain_text_search",
            Title = "Return plain text",
            Idempotent = true,
            ReadOnly = true,
            UseStructuredContent = false)]
        [Description("Return plain text content.")]
        public static string ReturnPlainText(
            [Description("Payload query text.")] string query)
            => $"plain:{query}";
    }

    private sealed record TestMcpSearchResult(string Query, string Source);
}
