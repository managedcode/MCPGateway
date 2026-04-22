#pragma warning disable MCPEXP001

using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerOptionsSetup(
    McpGatewayMcpServerHandlers handlers,
    McpGatewayMcpServerTaskStore taskStore
)
    : IPostConfigureOptions<McpServerOptions>
{
    public void PostConfigure(string? name, McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Logging ??= new LoggingCapability();
        options.Capabilities.Tools ??= new ToolsCapability();
        options.Capabilities.Prompts ??= new PromptsCapability();
        options.Capabilities.Prompts.ListChanged ??= true;
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Resources.Subscribe ??= true;
        options.Capabilities.Completions ??= new CompletionsCapability();
        options.Capabilities.Tasks ??= new McpTasksCapability();
        options.Capabilities.Tasks.Requests ??= new RequestMcpTasksCapability();
        options.Capabilities.Tasks.Requests.Tools ??= new ToolsMcpTasksCapability();
        options.Capabilities.Tasks.Requests.Tools.Call ??= new CallToolMcpTasksCapability();
        options.Capabilities.Tasks.List ??= new ListMcpTasksCapability();
        options.Capabilities.Tasks.Cancel ??= new CancelMcpTasksCapability();
        options.Handlers ??= new McpServerHandlers();
        options.TaskStore ??= taskStore;
        options.SendTaskStatusNotifications = true;

        options.Handlers.ListToolsHandler = handlers.ListToolsAsync;
        options.Handlers.CallToolHandler = handlers.CallToolAsync;
        options.Handlers.ListPromptsHandler = handlers.ListPromptsAsync;
        options.Handlers.GetPromptHandler = handlers.GetPromptAsync;
        options.Handlers.ListResourcesHandler = handlers.ListResourcesAsync;
        options.Handlers.ListResourceTemplatesHandler = handlers.ListResourceTemplatesAsync;
        options.Handlers.ReadResourceHandler = handlers.ReadResourceAsync;
        options.Handlers.CompleteHandler = handlers.CompleteAsync;
        options.Handlers.SubscribeToResourcesHandler = handlers.SubscribeToResourceAsync;
        options.Handlers.UnsubscribeFromResourcesHandler = handlers.UnsubscribeFromResourceAsync;
    }
}

#pragma warning restore MCPEXP001
