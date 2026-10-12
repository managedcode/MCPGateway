using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayEgressIntegrationTests
{
    [TUnit.Core.Test]
    public async Task Egress_checks_URI_and_resolved_IP_and_never_follows_credentialed_redirect()
    {
        var targetCalls = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapGet("/target", () => { Interlocked.Increment(ref targetCalls); return "allowed"; });
        server.MapGet("/redirect", (HttpContext context) => context.Response.Redirect("/target"));
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        var policy = new Policy();
        using var client = McpGatewayEgressHttpClient.Create("remote", policy, new Actor(), new SocketsHttpHandler());
        client.DefaultRequestHeaders.Add("X-Caller-Secret", "SEEDED_CREDENTIAL");
        await Assert.That(await client.GetStringAsync(new Uri(uri, "/target"))).IsEqualTo("allowed");
        await Assert.That(policy.Requests.Any(request => request.ResolvedAddress is not null)).IsTrue();
        await Assert.That(policy.Subjects.All(subject => subject == "alice")).IsTrue();
        try
        {
            using var response = await client.GetAsync(new Uri(uri, "/redirect"));
            Assert.Fail("A redirect must not be followed.");
        }
        catch (HttpRequestException exception)
        {
            await Assert.That(exception.Message).DoesNotContain("SEEDED_CREDENTIAL");
        }
        await Assert.That(targetCalls).IsEqualTo(1);
        policy.AllowAddresses = false;
        try
        {
            await client.GetStringAsync(new Uri(uri, "/target"));
            Assert.Fail("The resolved IP must be checked on every request.");
        }
        catch (HttpRequestException exception)
        {
            await Assert.That(exception.Message).Contains("unavailable");
        }
        await Assert.That(targetCalls).IsEqualTo(1);
        await server.StopAsync();
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "partition", "v1"));
    }
    private sealed class Policy : IMcpGatewayEgressPolicy
    {
        public bool AllowAddresses { get; set; } = true;
        public ConcurrentBag<McpGatewayEgressRequest> Requests { get; } = [];
        public ConcurrentBag<string> Subjects { get; } = [];
        public ValueTask<bool> IsAllowedAsync(McpGatewayExecutionContext context, McpGatewayEgressRequest request,
            CancellationToken cancellationToken = default)
        {
            Subjects.Add(context.SubjectId);
            Requests.Add(request);
            return ValueTask.FromResult(request.ResolvedAddress is null || AllowAddresses && IPAddress.IsLoopback(request.ResolvedAddress));
        }
    }
}
