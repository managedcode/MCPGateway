using System.Text.Json;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayJsonInvocationHttpTests
{
    [Test]
    public async Task Http_meta_invocation_preserves_native_json_context_and_independent_read_outcome()
    {
        await using var host = await JsonInvocationHttpHost.StartAsync();
        await using var client = await host.CreateClientAsync(JsonInvocationHttpHost.Owner);
        using var document = JsonDocument.Parse("""
            {"CamelCase":"Keep-CASE","integer":9007199254740993,"decimal":1234567890.1234567890123456789,"optional":null,"items":["Item",null,true,{"n":1.25}]}
            """);
        var payload = document.RootElement.Clone();
        var context = JsonSerializer.SerializeToElement(new { subjectId = "model-authored", optional = (string?)null }, McpGatewayJsonSerializer.Options);
        var result = await JsonInvocationHttpHost.InvokeAsync(client, JsonInvocationHttpHost.WriteTool.ToUpperInvariant(),
            new Dictionary<string, JsonElement>
            {
                [JsonInvocationHttpHost.PayloadKey] = payload,
                [JsonInvocationHttpHost.QueryKey] = JsonSerializer.SerializeToElement("explicit", McpGatewayJsonSerializer.Options),
            }, context, "mapped");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.ToolId).IsEqualTo(JsonInvocationHttpHost.WriteTool);
        await Assert.That(result.SourceId).IsEqualTo(JsonInvocationHttpHost.Source);

        var read = await JsonInvocationHttpHost.InvokeAsync(client, JsonInvocationHttpHost.ReadTool);
        await Assert.That(read.Error).IsNull();
        await Assert.That(read.IsSuccess).IsTrue();
        var snapshot = (JsonElement)read.Output!;
        await Assert.That(snapshot.GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That(snapshot.GetProperty("query").GetString()).IsEqualTo("explicit");
        var stored = snapshot.GetProperty("payload");
        await Assert.That(stored.GetProperty("CamelCase").GetString()).IsEqualTo("Keep-CASE");
        await Assert.That(stored.GetProperty("integer").GetInt64()).IsEqualTo(9007199254740993L);
        await Assert.That(stored.GetProperty("decimal").GetRawText()).IsEqualTo("1234567890.1234567890123456789");
        await Assert.That(stored.GetProperty("optional").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(stored.GetProperty("items")[1].ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(stored.GetProperty("items")[2].GetBoolean()).IsTrue();
        await Assert.That(stored.GetProperty("items")[3].GetProperty("n").GetDecimal()).IsEqualTo(1.25m);
        await Assert.That(snapshot.GetProperty("context").GetProperty("subjectId").GetString()).IsEqualTo("model-authored");
        await Assert.That(snapshot.GetProperty("context").GetProperty("optional").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Http_invalid_schema_case_collision_and_foreign_identity_cannot_change_committed_json()
    {
        await using var host = await JsonInvocationHttpHost.StartAsync();
        await using var owner = await host.CreateClientAsync(JsonInvocationHttpHost.Owner);
        await using var other = await host.CreateClientAsync(JsonInvocationHttpHost.Other);
        var original = JsonSerializer.SerializeToElement(new { value = "original" }, McpGatewayJsonSerializer.Options);
        var committed = await JsonInvocationHttpHost.InvokeAsync(owner, JsonInvocationHttpHost.WriteTool,
            new Dictionary<string, JsonElement> { [JsonInvocationHttpHost.PayloadKey] = original });
        await Assert.That(committed.IsSuccess).IsTrue();
        var before = await JsonInvocationHttpHost.InvokeAsync(owner, JsonInvocationHttpHost.ReadTool);
        await Assert.That(before.Error).IsNull();
        await Assert.That(before.IsSuccess).IsTrue();

        var invalid = await JsonInvocationHttpHost.InvokeAsync(owner, JsonInvocationHttpHost.WriteTool,
            new Dictionary<string, JsonElement> { [JsonInvocationHttpHost.PayloadKey] = JsonSerializer.SerializeToElement("invalid", McpGatewayJsonSerializer.Options) });
        await Assert.That(invalid.Outcome).IsEqualTo(McpGatewayInvocationOutcome.InvalidArguments);
        var colliding = await JsonInvocationHttpHost.InvokeAsync(owner, JsonInvocationHttpHost.WriteTool,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [JsonInvocationHttpHost.PayloadKey] = original,
                [JsonInvocationHttpHost.QueryKey] = JsonSerializer.SerializeToElement("one", McpGatewayJsonSerializer.Options),
                [JsonInvocationHttpHost.QueryKey.ToUpperInvariant()] = JsonSerializer.SerializeToElement("two", McpGatewayJsonSerializer.Options),
            });
        await Assert.That(colliding.Outcome).IsEqualTo(McpGatewayInvocationOutcome.InvalidArguments);
        var denied = await JsonInvocationHttpHost.InvokeAsync(other, JsonInvocationHttpHost.WriteTool,
            new Dictionary<string, JsonElement> { [JsonInvocationHttpHost.PayloadKey] = original },
            JsonSerializer.SerializeToElement(new { subjectId = JsonInvocationHttpHost.Owner }, McpGatewayJsonSerializer.Options));
        await Assert.That(denied.Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        var after = await JsonInvocationHttpHost.InvokeAsync(owner, JsonInvocationHttpHost.ReadTool);
        await Assert.That(after.Error).IsNull();
        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(((JsonElement)after.Output!).GetRawText()).IsEqualTo(((JsonElement)before.Output!).GetRawText());
    }
}
