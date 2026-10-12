using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayAuditIntegrationTests
{
    [TUnit.Core.Test]
    public async Task Audit_failure_preserves_completed_mutation_and_never_records_business_payload_or_retries()
    {
        var dispatches = 0;
        var tags = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        { if (instrument.Meter.Name == "ManagedCode.MCPGateway") { meter.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((_, _, values, _) => { foreach (var tag in values) { tags.Add($"{tag.Key}={tag.Value}"); } });
        listener.SetMeasurementEventCallback<double>((_, _, values, _) => { foreach (var tag in values) { tags.Add($"{tag.Key}={tag.Value}"); } });
        listener.Start();
        var audit = new Audit();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMcpGatewayAuditSink>(audit);
        services.AddMcpGateway(options => options.AddTool("operations", AIFunctionFactory.Create((string value) =>
        { Interlocked.Increment(ref dispatches); return new { value, secret = "SEEDED_PRIVATE_OUTPUT" }; }, new AIFunctionFactoryOptions { Name = "write" })));
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolName: "write",
            Arguments: new Dictionary<string, object?> { ["value"] = "SEEDED_PRIVATE_INPUT" }, Context: new Dictionary<string, object?> { ["credential"] = "SEEDED_CREDENTIAL" }));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(dispatches).IsEqualTo(1);
        var json = McpGatewayJsonSerializer.TrySerializeToElement(audit.Event)!.Value.GetRawText();
        await Assert.That(json).DoesNotContain("SEEDED_");
        await Assert.That(!tags.IsEmpty).IsTrue();
        await Assert.That(string.Join(',', tags)).DoesNotContain("SEEDED_");
        await Assert.That(audit.Event!.DefinitionHash!.Length).IsEqualTo(64);
        await Assert.That(audit.Event.DeliveredBytes > 0).IsTrue();
    }
    private sealed class Audit : IMcpGatewayAuditSink
    {
        public McpGatewayAuditEvent? Event { get; private set; }
        public ValueTask RecordAsync(McpGatewayExecutionContext context, McpGatewayAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Event = auditEvent;
            throw new IOException("SEEDED_AUDIT_BACKEND_FAILURE");
        }
    }
}
