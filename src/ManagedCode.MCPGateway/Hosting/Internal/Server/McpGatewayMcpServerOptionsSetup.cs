#pragma warning disable MCPEXP001

using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerOptionsSetup(
    McpGatewayMcpServerHandlers handlers,
    McpGatewayMcpServerSubscriptionCoordinator subscriptionCoordinator,
    McpGatewayMcpServerTaskStore taskStore,
    IOptions<McpGatewayOptions> gatewayOptions,
    McpGatewayGovernanceMessageFilter governanceFilter,
    IServiceProvider services
)
    : IPostConfigureOptions<McpServerOptions>
{
    public void PostConfigure(string? name, McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (taskStore is McpGatewayMcpServerTaskStore gatewayTaskStore)
        {
            gatewayTaskStore.Configure(gatewayOptions.Value.McpTaskStore);
            gatewayTaskStore.ConfigureAccess(services, gatewayOptions.Value.Governance.ManagedCatalog);
        }
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        options.Capabilities.Prompts ??= new PromptsCapability();
        options.Capabilities.Prompts.ListChanged ??= true;
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Resources.Subscribe ??= true;
        options.Capabilities.Completions ??= new CompletionsCapability();
        options.Handlers ??= new McpServerHandlers();

        options.Handlers.ListToolsHandler = handlers.ListToolsAsync;
        options.Handlers.CallToolHandler = handlers.CallToolAsync;
        options.Handlers.ListPromptsHandler = handlers.ListPromptsAsync;
        options.Handlers.GetPromptHandler = handlers.GetPromptAsync;
        options.Handlers.ListResourcesHandler = handlers.ListResourcesAsync;
        options.Handlers.ListResourceTemplatesHandler = handlers.ListResourceTemplatesAsync;
        options.Handlers.ReadResourceHandler = handlers.ReadResourceAsync;
        options.Handlers.CompleteHandler = handlers.CompleteAsync;
        options.Filters.Message.IncomingFilters.Add(governanceFilter.Create());
        options.Filters.Message.IncomingFilters.Add(
            subscriptionCoordinator.CreateIncomingFilter()
        );
        options.Filters.Message.OutgoingFilters.Add(
            subscriptionCoordinator.CreateOutgoingFilter()
        );
    }
}

#pragma warning restore MCPEXP001
