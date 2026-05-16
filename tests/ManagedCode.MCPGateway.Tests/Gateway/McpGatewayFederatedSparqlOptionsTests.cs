using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayFederatedSparqlOptionsTests
{
    [Test]
    public async Task Defaults_FederatedSparqlTimeoutUsesThirtySecondTimeSpan()
    {
        var options = new McpGatewayOptions();

        await Assert
            .That(options.MarkdownLdFederatedSparqlQueryTimeout)
            .IsEqualTo(McpGatewayOptions.DefaultMarkdownLdFederatedSparqlQueryTimeout);
        await Assert
            .That(McpGatewayOptions.DefaultMarkdownLdFederatedSparqlQueryTimeout)
            .IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task FederatedSparqlTimeout_CanBeOverriddenOrDisabled()
    {
        var options = new McpGatewayOptions
        {
            MarkdownLdFederatedSparqlQueryTimeout = TimeSpan.FromMinutes(2),
        };

        await Assert
            .That(options.MarkdownLdFederatedSparqlQueryTimeout)
            .IsEqualTo(TimeSpan.FromMinutes(2));

        options.MarkdownLdFederatedSparqlQueryTimeout = null;

        await Assert.That(options.MarkdownLdFederatedSparqlQueryTimeout).IsNull();
    }

    [Test]
    public async Task Gateway_RejectsInvalidFederatedSparqlTimeout()
    {
        await using var serviceProvider = GatewayTestServiceProviderFactory.Create(options =>
        {
            options.MarkdownLdFederatedSparqlQueryTimeout = TimeSpan.Zero;
        });

        ArgumentOutOfRangeException? exception = null;
        try
        {
            _ = serviceProvider.GetRequiredService<IMcpGateway>();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert
            .That(exception!.ParamName)
            .IsEqualTo(nameof(McpGatewayOptions.MarkdownLdFederatedSparqlQueryTimeout));
    }
}
