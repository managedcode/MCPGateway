using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class JsonInvocationHttpHost(WebApplication application) : IAsyncDisposable
{
    public const string Owner = "json-owner";
    public const string Other = "json-other";
    public const string Source = "json-source";
    public const string WriteTool = "json_commit";
    public const string ReadTool = "json_read";
    public const string PayloadKey = "payload";
    public const string QueryKey = "query";
    private const string ContextKey = "context";
    private const string ToolIdKey = "toolId";
    private const string ArgumentsKey = "arguments";
    private const string ActorHeader = "X-Json-Actor";
    private const string AuthenticationType = "test-json-http";
    private const string Endpoint = "/mcp";
    private const string Tenant = "json-tenant";
    private const string PolicyRevision = "json-policy-1";

    public static async Task<JsonInvocationHttpHost> StartAsync()
    {
        var state = new JsonRecord();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IMcpGatewayExecutionContextProvider, Actor>();
        builder.Services.AddScoped<IMcpGatewayAccessPolicy, Policy>();
        builder.Services.AddMcpGateway(options =>
        {
            options.AddTool(Source, new CommitFunction(state));
            options.AddTool(Source, AIFunctionFactory.Create(state.Read, new AIFunctionFactoryOptions { Name = ReadTool }));
        });
        builder.Services.AddMcpServer().WithMcpGatewayCatalog().WithHttpTransport(options => options.Stateless = true);
        builder.Services.Configure<McpGatewayMcpServerExportOptions>(options => options.Mode = McpGatewayMcpServerExportMode.DiscoveryOnly);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, context.Request.Headers[ActorHeader].ToString())], AuthenticationType));
            await next(context);
        });
        app.MapMcp(Endpoint);
        await app.StartAsync();
        return new JsonInvocationHttpHost(app);
    }

    public async Task<McpClient> CreateClientAsync(string actor)
    {
        var client = application.GetTestClient();
        client.DefaultRequestHeaders.Add(ActorHeader, actor);
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(client.BaseAddress!, Endpoint) }, client, application.Services.GetRequiredService<ILoggerFactory>(), true));
    }

    public static async Task<McpGatewayInvokeResult> InvokeAsync(McpClient client, string toolId,
        Dictionary<string, JsonElement>? arguments = null, JsonElement? context = null, string? query = null)
    {
        var result = await client.CallToolAsync(McpGatewayToolSet.DefaultInvokeToolName,
            new Dictionary<string, object?>
            {
                [ToolIdKey] = toolId,
                [ArgumentsKey] = arguments,
                [ContextKey] = context,
                [QueryKey] = query,
            });
        return JsonSerializer.Deserialize<McpGatewayInvokeResult>(result.Content.OfType<TextContentBlock>().Single().Text,
            McpGatewayJsonSerializer.Options) ?? throw new InvalidOperationException("HTTP invocation returned no gateway outcome.");
    }

    public async ValueTask DisposeAsync() => await application.DisposeAsync();

    private sealed class Actor(IHttpContextAccessor accessor) : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var subject = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            return ValueTask.FromResult(new McpGatewayExecutionContext(subject, Tenant, subject, PolicyRevision)
            { IsAuthenticated = !string.IsNullOrEmpty(subject) });
        }
    }

    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context,
            McpGatewayAccessRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.SubjectId == Owner
                ? McpGatewayAccessDecision.Allowed with { IsReadOnly = request.AssetId == ReadTool }
                : McpGatewayAccessDecision.Denied);
    }

    private sealed class JsonRecord
    {
        private int _count;
        private JsonElement _payload;
        private JsonElement _context = JsonSerializer.SerializeToElement<object?>(null);
        private string _query = string.Empty;
        public JsonRecordSnapshot Commit(AIFunctionArguments arguments)
        {
            _payload = ((JsonElement)arguments[PayloadKey]!).Clone();
            _context = arguments.TryGetValue(ContextKey, out var context) ? ((JsonElement)context!).Clone() : JsonSerializer.SerializeToElement<object?>(null);
            _query = arguments.TryGetValue(QueryKey, out var query) ? ((JsonElement)query!).GetString() ?? string.Empty : string.Empty;
            _count++;
            return Read();
        }
        public JsonRecordSnapshot Read() => new(_count, _payload, _context, _query);
    }

    private sealed record JsonRecordSnapshot(int Count, JsonElement Payload, JsonElement Context, string Query);

    private sealed class CommitFunction(JsonRecord state) : AIFunction
    {
        public override string Name => WriteTool;
        public override string Description => "Commit caller JSON to the test record.";
        public override JsonElement JsonSchema { get; } = JsonDocument.Parse("""
            {"type":"object","required":["payload"],"additionalProperties":false,"properties":{"payload":{"type":"object"},"context":{"type":"object"},"query":{"type":"string"}}}
            """).RootElement.Clone();
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(state.Commit(arguments));
    }
}
