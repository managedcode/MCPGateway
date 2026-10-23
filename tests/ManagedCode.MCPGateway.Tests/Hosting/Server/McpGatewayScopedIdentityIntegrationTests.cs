using System.Security.Claims;
using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayScopedIdentityIntegrationTests
{
    private static readonly string[] People = ["alice", "bob"];
    [Test]
    public async Task Parallel_SDK_HTTP_requests_use_their_scoped_host_identity_and_ignore_model_impersonation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IMcpGatewayExecutionContextProvider, Actor>();
        builder.Services.AddScoped<IMcpGatewayAccessPolicy, Policy>();
        builder.Services.AddMcpGateway(options =>
        {
            foreach (var person in People)
            {
                options.AddTool(person, TestFunctionFactory.CreateFunction(static (string query) => query, person + "_search", "Find records"));
            }
        });
        builder.Services.AddMcpServer().WithMcpGatewayCatalog().WithHttpTransport(options => options.Stateless = true);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, context.Request.Headers["X-Actor"].ToString())], "host"));
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        using var aliceHttp = app.GetTestClient();
        using var bobHttp = app.GetTestClient();
        aliceHttp.DefaultRequestHeaders.Add("X-Actor", "alice");
        bobHttp.DefaultRequestHeaders.Add("X-Actor", "bob");
        await using var alice = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, aliceHttp));
        await using var bob = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri("http://localhost/mcp") }, bobHttp));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var person = index % 2 == 0 ? "alice" : "bob";
            var client = person == "alice" ? alice : bob;
            var listed = await client.ListToolsAsync();
            await Assert.That(listed.Select(tool => tool.Name)).IsEquivalentTo([person + "_search"]);
            var result = await client.CallToolAsync(person + "_search", new Dictionary<string, object?> { ["query"] = "safe" });
            await Assert.That(result.IsError is true).IsFalse();
            var denied = await app.Services.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "alice_search")
            { Context = new Dictionary<string, JsonElement> { ["subjectId"] = JsonSerializer.SerializeToElement("alice", McpGatewayJsonSerializer.Options) } });
            await Assert.That(denied.Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        }));
        await app.StopAsync();
    }
    private sealed class Actor(IHttpContextAccessor accessor) : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var subject = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            return ValueTask.FromResult(new McpGatewayExecutionContext(subject, "tenant", subject, "v1") { IsAuthenticated = subject != "anonymous" });
        }
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(
                context.IsAuthenticated && (request.SourceId is null || request.SourceId == context.SubjectId)));
    }
}
