using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayJsonSerializerTests
{
    [TUnit.Core.Test]
    public async Task TrySerializeToElement_NormalizesJsonNodeAndUnsupportedValues()
    {
        var node = JsonNode.Parse("""{"status":"open","count":2}""");

        var serializedNode = McpGatewayJsonSerializer.TrySerializeToElement(node);
        var serializedNullDocument = McpGatewayJsonSerializer.TrySerializeToElement(
            JsonDocument.Parse("null")
        );
        var unsupportedValue = McpGatewayJsonSerializer.TrySerializeToElement(static () => 42);

        await Assert.That(serializedNode.HasValue).IsTrue();
        await Assert
            .That(serializedNode!.Value.GetProperty("status").GetString())
            .IsEqualTo("open");
        await Assert.That(serializedNode.Value.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(serializedNullDocument).IsNull();
        await Assert.That(unsupportedValue).IsNull();
    }

    [TUnit.Core.Test]
    public async Task TrySerializeToNode_HandlesJsonElementJsonDocumentAndUnsupportedValues()
    {
        var element = JsonSerializer.SerializeToElement(new { status = "open", count = 2 });

        var serializedElement = McpGatewayJsonSerializer.TrySerializeToNode(element);
        var serializedDocument = McpGatewayJsonSerializer.TrySerializeToNode(
            JsonDocument.Parse("""{"kind":"document"}""")
        );
        var serializedNullDocument = McpGatewayJsonSerializer.TrySerializeToNode(
            JsonDocument.Parse("null")
        );
        var unsupportedValue = McpGatewayJsonSerializer.TrySerializeToNode(static () => 42);

        await Assert.That(serializedElement).IsNotNull();
        await Assert.That(serializedElement!["status"]!.GetValue<string>()).IsEqualTo("open");
        await Assert.That(serializedElement["count"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(serializedDocument).IsNotNull();
        await Assert.That(serializedDocument!["kind"]!.GetValue<string>()).IsEqualTo("document");
        await Assert.That(serializedNullDocument).IsNull();
        await Assert.That(unsupportedValue).IsNull();
    }
}
