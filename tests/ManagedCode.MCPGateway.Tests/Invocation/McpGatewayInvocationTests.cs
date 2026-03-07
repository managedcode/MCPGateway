using System.ComponentModel;
using System.Text.Json;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayInvocationTests
{
    private static string TextUppercase([Description("Text to uppercase.")] string query) => query.ToUpperInvariant();

    private static string OptionalQueryEcho([Description("Text to uppercase.")] string? query = null)
        => (query ?? "missing").ToUpperInvariant();

    private static string EchoContextSummary(
        [Description("Main query text.")] string query,
        [Description("Execution context summary.")] string contextSummary)
        => $"{query}|{contextSummary}";

    private static string ReadStructuredContext([Description("Structured execution context.")] JsonElement context)
        => $"{context.GetProperty("domain").GetString()}|{context.GetProperty("page").GetString()}";

    private static string AlphaSharedSearch([Description("Shared query text.")] string query) => $"alpha:{query}";

    private static string BetaSharedSearch([Description("Shared query text.")] string query) => $"beta:{query}";

    private static JsonElement ReturnJsonString() => JsonSerializer.SerializeToElement("done");

    private static string ThrowingTool() => throw new InvalidOperationException("boom");

    private static JsonElement GetJsonProperty(JsonElement element, string name)
        => element.EnumerateObject()
            .First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;
}
