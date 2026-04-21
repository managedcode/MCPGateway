using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerOptionsSetup(McpGatewayMcpServerHandlers handlers)
    : IPostConfigureOptions<McpServerOptions>
{
    public void PostConfigure(string? name, McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        options.Capabilities.Prompts ??= new PromptsCapability();
        options.Handlers ??= new McpServerHandlers();

        options.Handlers.ListToolsHandler = handlers.ListToolsAsync;
        options.Handlers.CallToolHandler = handlers.CallToolAsync;
        options.Handlers.ListPromptsHandler = handlers.ListPromptsAsync;
        options.Handlers.GetPromptHandler = handlers.GetPromptAsync;
    }
}
