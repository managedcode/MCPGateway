using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMetaToolNamingTests
{
    [TUnit.Core.Test]
    public async Task CreateTools_HashShortensLongCustomMetaToolNames()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var toolNames = toolSet
            .CreateTools(
                CreateLongName("search", 'a'),
                CreateLongName("route", 'b'),
                CreateLongName("invoke", 'c')
            )
            .Select(static tool => tool.Name)
            .ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(3);
        await Assert.That(toolNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(3);
        await Assert.That(toolNames.All(IsMcpSafeName)).IsTrue();
        await Assert.That(toolNames.All(HasHashSuffix)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateTools_SourceQualifiesCaseInsensitiveCustomMetaToolNameCollisions()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var toolNames = toolSet
            .CreateTools("Gateway Search", "gateway-search", "Gateway/Search")
            .Select(static tool => tool.Name)
            .ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(3);
        await Assert.That(toolNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(3);
        await Assert.That(toolNames).Contains("gateway_search");
        await Assert.That(toolNames).Contains("gateway-search");
        await Assert
            .That(toolNames.Any(static name => name.StartsWith("gateway_search_", StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(toolNames.All(IsMcpSafeName)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task CreateGraphTools_HashShortensLongCustomGraphToolNames()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();

        var toolNames = toolSet
            .CreateGraphTools(
                CreateLongName("graph-search", 'd'),
                CreateLongName("graph-federated", 'e'),
                CreateLongName("graph-export", 'f'),
                CreateLongName("graph-schema", 'a'),
                CreateLongName("tool-index", 'b')
            )
            .Select(static tool => tool.Name)
            .ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(5);
        await Assert.That(toolNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(5);
        await Assert.That(toolNames.All(IsMcpSafeName)).IsTrue();
        await Assert.That(toolNames.All(HasHashSuffix)).IsTrue();
    }

    [TUnit.Core.Test]
    public async Task AddTools_TreatsExistingNormalizedNamesAsReserved()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(static _ => { });
        var toolSet = serviceProvider.GetRequiredService<McpGatewayToolSet>();
        var existingTools = toolSet.CreateTools("Gateway Search", "Gateway Route", "Gateway Invoke").ToList();

        var composedTools = toolSet.AddTools(
            existingTools,
            "gateway_search",
            "gateway_route",
            "gateway_invoke"
        );
        var toolNames = composedTools.Select(static tool => tool.Name).ToArray();

        await Assert.That(toolNames.Length).IsEqualTo(3);
        await Assert.That(toolNames).IsEquivalentTo(["gateway_search", "gateway_route", "gateway_invoke"]);
    }

    private static string CreateLongName(string role, char marker) =>
        string.Concat("Gateway ", role, " Tool ", new string(marker, 100));

    private static bool IsMcpSafeName(string name) =>
        name.Length is > 0 and <= McpGatewayProtocolName.MaxNameLength
        && name.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character is '_' or '-'
        );

    private static bool HasHashSuffix(string name)
    {
        if (name.Length < 9 || name[^9] != '_')
        {
            return false;
        }

        return name[^8..].All(static character =>
            character is >= 'a' and <= 'f' || character is >= '0' and <= '9'
        );
    }
}
