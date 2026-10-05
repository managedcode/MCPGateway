using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayInvocationPrivacyTests
{
    private const string SourceId = "private-source";
    private const string ToolName = "private_tool";
    private const string ToolDescription = "Exercise safe failure reporting.";
    private const string PrivateArgumentName = "privateArg";
    private const string QueryMarker = "query-marker-314159";
    private const string ArgumentMarker = "argument-marker-271828";
    private const string ExceptionMarker = "provider-marker-161803";
    private const string FatalMarker = "fatal-marker-141421";
    private const string ExpectedFailure = "Tool invocation failed.";

    [TUnit.Core.Test]
    public async Task InvokeAsync_UsesSafeFailureResultAndLoggerState()
    {
        using var logs = new GatewayCapturingLoggerProvider();
        await using var services = CreateServices(logs, ThrowSensitiveFailure);
        var gateway = services.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();

        var result = await gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: ToolName,
                Query: QueryMarker,
                Arguments: new Dictionary<string, object?>
                {
                    [PrivateArgumentName] = ArgumentMarker,
                }
            )
        );

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).IsEqualTo(ExpectedFailure);
        await Assert.That(logs.Entries.All(static entry => entry.ExceptionText is null)).IsTrue();
        await AssertNoSensitiveMarkersAsync(
            string.Join(Environment.NewLine, logs.Entries.Select(FlattenLog))
        );
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PropagatesDirectFatalException()
    {
        using var logs = new GatewayCapturingLoggerProvider();
        await using var services = CreateServices(logs, ThrowFatalFailure);
        var gateway = services.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var logCountBeforeInvocation = logs.Entries.Count;

        var exception = await Assert.ThrowsAsync<OutOfMemoryException>(async () =>
            await gateway.InvokeAsync(
                new McpGatewayInvokeRequest(ToolId: ToolName, Query: "fatal query")
            )
        );

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).IsEqualTo(FatalMarker);
        await Assert.That(logs.Entries.Count).IsEqualTo(logCountBeforeInvocation);
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PropagatesFatalExceptionInsideAggregate()
    {
        using var logs = new GatewayCapturingLoggerProvider();
        await using var services = CreateServices(logs, ThrowAggregateFatalFailure);
        var gateway = services.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        var logCountBeforeInvocation = logs.Entries.Count;

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await gateway.InvokeAsync(
                new McpGatewayInvokeRequest(ToolId: ToolName, Query: "fatal query")
            )
        );

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.ToString()).Contains(FatalMarker);
        await Assert.That(logs.Entries.Count).IsEqualTo(logCountBeforeInvocation);
    }

    [TUnit.Core.Test]
    public async Task InvokeAsync_PreservesCallerCancellation()
    {
        using var logs = new GatewayCapturingLoggerProvider();
        await using var services = CreateServices(logs, EchoQuery);
        var gateway = services.GetRequiredService<IMcpGateway>();
        await gateway.BuildIndexAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await gateway.InvokeAsync(
                new McpGatewayInvokeRequest(ToolId: ToolName),
                cancellation.Token
            )
        );
    }

    private static ServiceProvider CreateServices(
        GatewayCapturingLoggerProvider logs,
        Delegate callback
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddProvider(logs);
            logging.SetMinimumLevel(LogLevel.Trace);
        });
        services.AddMcpGateway(options =>
            options.AddTool(
                SourceId,
                TestFunctionFactory.CreateFunction(callback, ToolName, ToolDescription)
            )
        );
        return services.BuildServiceProvider();
    }

    private static string ThrowSensitiveFailure(string query, string privateArg) =>
        throw new InvalidOperationException(
            string.Join(':', ExceptionMarker, query, privateArg)
        );

    private static string ThrowFatalFailure(string query) => throw CreateFatalFailure();

    private static string ThrowAggregateFatalFailure(string query) =>
        throw new AggregateException(FatalMarker, CreateFatalFailure());

    private static Exception CreateFatalFailure() =>
        (Exception)Activator.CreateInstance(typeof(OutOfMemoryException), FatalMarker)!;

    private static string EchoQuery(string query) => query;

    private static string FlattenLog(GatewayCapturedLog entry) =>
        string.Join(Environment.NewLine, entry.Category, entry.Message, entry.ExceptionText);

    private static async Task AssertNoSensitiveMarkersAsync(string value)
    {
        await Assert.That(value.Contains(QueryMarker, StringComparison.Ordinal)).IsFalse();
        await Assert.That(value.Contains(ArgumentMarker, StringComparison.Ordinal)).IsFalse();
        await Assert.That(value.Contains(ExceptionMarker, StringComparison.Ordinal)).IsFalse();
    }
}
