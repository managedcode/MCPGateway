using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayStdioToolSourceRegistrationTests
{
    [Test]
    public async Task CreateTransportOptions_UsesConfiguredSdkStdioOptions()
    {
        var stderrLines = new List<string>();
        Action<string> stderrCallback = stderrLines.Add;
        var environmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environmentVariables["MCP_ENVIRONMENT"] = "test";
        var options = new McpGatewayStdioServerOptions
        {
            SourceId = "filesystem",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
            WorkingDirectory = "/tmp/mcp",
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environmentVariables,
            ShutdownTimeout = TimeSpan.FromSeconds(9),
            StandardErrorLines = stderrCallback,
        };

        var transportOptions = McpGatewayStdioToolSourceRegistration.CreateTransportOptions(options);

        await Assert.That(transportOptions.Name).IsEqualTo("filesystem");
        await Assert.That(transportOptions.Command).IsEqualTo("npx");
        await Assert
            .That(transportOptions.Arguments)
            .IsEquivalentTo(["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]);
        await Assert.That(transportOptions.WorkingDirectory).IsEqualTo("/tmp/mcp");
        await Assert.That(transportOptions.InheritEnvironmentVariables).IsFalse();
        await Assert.That(transportOptions.EnvironmentVariables!["MCP_ENVIRONMENT"]).IsEqualTo("test");
        await Assert.That(transportOptions.ShutdownTimeout).IsEqualTo(TimeSpan.FromSeconds(9));
        await Assert.That(ReferenceEquals(transportOptions.StandardErrorLines, stderrCallback)).IsTrue();
    }

    [Test]
    public async Task CreateTransportOptions_PreservesSdkDefaultsWhenOptionalValuesAreUnset()
    {
        var sdkDefaults = new StdioClientTransportOptions { Command = "dotnet" };
        var transportOptions = McpGatewayStdioToolSourceRegistration.CreateTransportOptions(
            new McpGatewayStdioServerOptions
            {
                SourceId = "stdio",
                Command = "dotnet",
            }
        );

        await Assert.That(transportOptions.InheritEnvironmentVariables).IsTrue();
        await Assert.That(transportOptions.ShutdownTimeout).IsEqualTo(sdkDefaults.ShutdownTimeout);
        await Assert.That(transportOptions.EnvironmentVariables).IsEmpty();
        await Assert.That(transportOptions.StandardErrorLines).IsNull();
    }
}
