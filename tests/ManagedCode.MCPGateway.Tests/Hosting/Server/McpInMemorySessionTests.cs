using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpInMemorySessionTests
{
    private static readonly string[] IdentityToolNames = ["identity"];

    [Test]
    public async Task ParallelSessions_ShouldDiscoverAndCallWithTheirOwnIdentityAsync()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var firstPrincipal = Principal("first-actor");
        var secondPrincipal = Principal("second-actor");
        await using var first = await McpInMemorySession.CreateAsync(
            Options(firstPrincipal), services, loggerFactory, lifetime.Token);
        await using var second = await McpInMemorySession.CreateAsync(
            Options(secondPrincipal), services, loggerFactory, lifetime.Token);

        var catalogs = await Task.WhenAll(
            first.Client.ListToolsAsync(cancellationToken: lifetime.Token).AsTask(),
            second.Client.ListToolsAsync(cancellationToken: lifetime.Token).AsTask());
        var results = await Task.WhenAll(
            first.Client.CallToolAsync(new CallToolRequestParams { Name = "identity" }, lifetime.Token).AsTask(),
            second.Client.CallToolAsync(new CallToolRequestParams { Name = "identity" }, lifetime.Token).AsTask());

        await Assert.That(catalogs[0].Select(tool => tool.Name)).IsEquivalentTo(IdentityToolNames);
        await Assert.That(catalogs[1].Select(tool => tool.Name)).IsEquivalentTo(IdentityToolNames);
        await Assert.That(results[0].Content.OfType<TextContentBlock>().Single().Text).IsEqualTo("first-actor");
        await Assert.That(results[1].Content.OfType<TextContentBlock>().Single().Text).IsEqualTo("second-actor");
    }

    [Test]
    public async Task CancelledConnection_ShouldTerminateWithoutStartingASessionAsync()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var lifetime = new CancellationTokenSource();
        await lifetime.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await using var session = await McpInMemorySession.CreateAsync(
                Options(Principal("cancelled-actor")), services,
                services.GetRequiredService<ILoggerFactory>(), lifetime.Token);
        });
    }

    [Test]
    public async Task ConcurrentDisposal_ShouldCompleteForEveryCallerAsync()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var session = await McpInMemorySession.CreateAsync(
            Options(Principal("actor")), services,
            services.GetRequiredService<ILoggerFactory>(),
            TestContext.Current!.Execution.CancellationToken);

        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        await session.DisposeAsync();
    }

    [Test]
    public async Task CancellingSessionOperation_ShouldCancelItsHandlerAndKeepOtherSessionUsableAsync()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(Principal("cancelled-actor"));
        options.Handlers.CallToolHandler = async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new CallToolResult();
            }
            finally
            {
                if (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                }
            }
        };
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        await using var blocked = await McpInMemorySession.CreateAsync(
            options, services, loggerFactory, callCancellation.Token);
        await using var independent = await McpInMemorySession.CreateAsync(
            Options(Principal("independent-actor")), services, loggerFactory, deadline.Token);

        var call = blocked.Client.CallToolAsync(
            new CallToolRequestParams { Name = "identity" }, callCancellation.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        await callCancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await call);
        await cancelled.Task.WaitAsync(deadline.Token);
        var result = await independent.Client.CallToolAsync(
            new CallToolRequestParams { Name = "identity" }, deadline.Token);
        await Assert.That(result.Content.OfType<TextContentBlock>().Single().Text)
            .IsEqualTo("independent-actor");
    }

    private static ClaimsPrincipal Principal(string actor) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], "test"));

    private static McpServerOptions Options(ClaimsPrincipal principal) => new()
    {
        ServerInfo = new Implementation { Name = "in-memory-regression", Version = "1.0" },
        ScopeRequests = false,
        Filters = new McpServerFilters
        {
            Message = new McpMessageFilters
            {
                IncomingFilters = [next => (context, token) =>
                {
                    context.User = principal;
                    return next(context, token);
                }]
            }
        },
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = static (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool
                {
                    Name = "identity",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                }]
            }),
            CallToolHandler = static (context, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock
                {
                    Text = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "missing-actor"
                }]
            })
        }
    };
}
