using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerHandlers(
    McpGatewayMcpServerBindingManager bindingManager,
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory,
    IOptions<McpGatewayMcpServerExportOptions> exportOptions
)
{
    private const string ResultReaderNameConflictMessage = "A catalog tool collides with the reserved stored-result reader name.";
    public async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        if (exportOptions.Value.Mode == McpGatewayMcpServerExportMode.DiscoveryOnly)
        {
            return new ListToolsResult { Tools = CreateDiscoveryTools(binding.Binding).Select(tool => tool.ProtocolTool).ToList() };
        }

        var descriptors = await binding.Binding.Gateway.ListToolsAsync(cancellationToken);
        var tools = descriptors.Select(McpGatewayMcpServerProtocolMapper.ToProtocolTool).ToList();
        var resultReader = CreateDiscoveryTools(binding.Binding).FirstOrDefault(tool => tool.ProtocolTool.Name == McpGatewayDiscoveryToolSet.ResultToolName);
        if (resultReader is not null)
        {
            if (tools.Any(tool => tool.Name.Equals(McpGatewayDiscoveryToolSet.ResultToolName, StringComparison.OrdinalIgnoreCase)))
            { throw new InvalidOperationException(ResultReaderNameConflictMessage); }
            tools.Add(resultReader.ProtocolTool);
        }
        return new ListToolsResult { Tools = tools };
    }

    public async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var toolId = request.Params?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return McpGatewayMcpServerProtocolMapper.CreateErrorToolResult(
                McpGatewayMcpProtocolConstants.InvalidToolNameMessage
            );
        }

        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        if (exportOptions.Value.Mode == McpGatewayMcpServerExportMode.DiscoveryOnly)
        {
            var tool = CreateDiscoveryTools(binding.Binding).FirstOrDefault(candidate => candidate.ProtocolTool.Name.Equals(toolId, StringComparison.OrdinalIgnoreCase));
            return tool is null
                ? McpGatewayMcpServerProtocolMapper.CreateErrorToolResult(McpGatewayMcpProtocolConstants.InvalidToolNameMessage)
                : await tool.InvokeAsync(request, cancellationToken);
        }

        if (toolId.Equals(McpGatewayDiscoveryToolSet.ResultToolName, StringComparison.OrdinalIgnoreCase))
        {
            var reader = CreateDiscoveryTools(binding.Binding).FirstOrDefault(tool => tool.ProtocolTool.Name == McpGatewayDiscoveryToolSet.ResultToolName);
            if (reader is not null)
            { return await reader.InvokeAsync(request, cancellationToken); }
        }

        var resolvedTool = await McpGatewayMcpServerRequestResolver.ResolveToolAsync(
            binding.Binding,
            toolId,
            cancellationToken
        );
        if (resolvedTool is null)
        {
            return McpGatewayMcpServerProtocolMapper.CreateErrorToolResult(
                $"Tool '{toolId}' was not found."
            );
        }

        var arguments = McpGatewayMcpServerProtocolMapper.ConvertArguments(request.Params?.Arguments);
        var invokeResult = await binding.Binding.Gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: resolvedTool.ToolId,
                Arguments: arguments
            ),
            cancellationToken
        );

        return McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(invokeResult);
    }

    private IEnumerable<McpServerTool> CreateDiscoveryTools(IMcpGatewayServerBinding binding) =>
        new McpGatewayDiscoveryToolSet(binding.Gateway, new McpGatewayDiscoveryService(binding.Gateway, serviceProvider), serviceProvider)
            .CreateTools().Cast<AIFunction>().Select(function => McpServerTool.Create(function));

    public async ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var descriptors = await binding.Binding.PromptCatalog.ListPromptsAsync(cancellationToken);
        return new ListPromptsResult
        {
            Prompts = descriptors
                .Select(McpGatewayMcpServerProtocolMapper.ToProtocolPrompt)
                .ToList(),
        };
    }

    public async ValueTask<GetPromptResult> GetPromptAsync(
        RequestContext<GetPromptRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var exportedPromptName = request.Params?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(exportedPromptName))
        {
            return McpGatewayMcpServerProtocolMapper.CreateErrorPromptResult(
                McpGatewayMcpProtocolConstants.InvalidPromptNameMessage
            );
        }

        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var resolvedRequest = await McpGatewayMcpServerRequestResolver.ResolvePromptAsync(
            binding.Binding,
            exportedPromptName,
            cancellationToken
        );
        if (resolvedRequest is null)
        {
            return McpGatewayMcpServerProtocolMapper.CreateErrorPromptResult(
                $"Prompt '{exportedPromptName}' was not found."
            );
        }

        var promptResult = await binding.Binding.PromptCatalog.GetPromptAsync(
            new McpGatewayPromptRequest(
                SourceId: resolvedRequest.SourceId,
                PromptName: resolvedRequest.PromptName,
                Arguments: McpGatewayMcpServerProtocolMapper.ConvertArguments(
                    request.Params?.Arguments
                )
            ),
            cancellationToken
        );

        return promptResult is null
            ? McpGatewayMcpServerProtocolMapper.CreateErrorPromptResult(
                $"Prompt '{exportedPromptName}' was not found."
            )
            : promptResult.ProtocolResult;
    }

    public async ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var descriptors = await binding.Binding.ResourceCatalog.ListResourcesAsync(cancellationToken);
        return new ListResourcesResult
        {
            Resources = descriptors
                .Select(McpGatewayMcpServerProtocolMapper.ToProtocolResource)
                .ToList(),
        };
    }

    public async ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(
        RequestContext<ListResourceTemplatesRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var descriptors = await binding.Binding.ResourceCatalog.ListResourceTemplatesAsync(
            cancellationToken
        );
        return new ListResourceTemplatesResult
        {
            ResourceTemplates = descriptors
                .Select(McpGatewayMcpServerProtocolMapper.ToProtocolResourceTemplate)
                .ToList(),
        };
    }

    public async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var requestedUri = request.Params?.Uri?.Trim();
        if (string.IsNullOrWhiteSpace(requestedUri))
        {
            throw new McpException(McpGatewayMcpProtocolConstants.InvalidResourceUriMessage);
        }

        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var resolvedRequest =
            await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
                binding.Binding,
                requestedUri,
                cancellationToken
            )
            ?? throw new McpException($"Resource '{requestedUri}' was not found.");

        var resourceResult =
            await binding.Binding.ResourceCatalog.ReadResourceAsync(
                new McpGatewayResourceRequest(
                    resolvedRequest.SourceId,
                    resolvedRequest.UpstreamUri
                ),
                cancellationToken
            ) ?? throw new McpException($"Resource '{requestedUri}' was not found.");

        return new ReadResourceResult
        {
            Contents = resourceResult
                .Contents.Select(content =>
                    McpGatewayMcpServerProtocolMapper.ToProtocolResourceContent(
                        content,
                        resolvedRequest
                    )
                )
                .ToList(),
        };
    }

    public async ValueTask<CompleteResult> CompleteAsync(
        RequestContext<CompleteRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var reference =
            request.Params?.Ref
            ?? throw new McpException(McpGatewayMcpProtocolConstants.InvalidCompletionReferenceMessage);
        var argument = request.Params?.Argument;
        if (argument is null || string.IsNullOrWhiteSpace(argument.Name))
        {
            throw new McpException(McpGatewayMcpProtocolConstants.InvalidCompletionArgumentMessage);
        }

        await using var binding = await bindingManager.AcquireAsync(
            request.Services,
            serviceProvider,
            request.Server,
            cancellationToken
        );

        var resolvedRequest =
            await McpGatewayMcpServerRequestResolver.ResolveCompletionAsync(
                binding.Binding,
                reference,
                cancellationToken
            )
            ?? throw new McpException("The requested completion target was not found.");

        var asset = reference is PromptReference prompt ? prompt.Name
            : ((ResourceTemplateReference)reference).Uri;
        await McpGatewayAccessBoundary.DemandAsync(serviceProvider, McpGatewayOperation.CompleteArgument,
            asset ?? throw new McpException(McpGatewayMcpProtocolConstants.InvalidCompletionReferenceMessage),
            resolvedRequest.Source.SourceId, cancellationToken);
        return await resolvedRequest.Source.CompleteAsync(
                resolvedRequest.UpstreamReference,
                McpGatewayMcpServerProtocolMapper.CloneArgument(argument),
                McpGatewayMcpServerProtocolMapper.CloneContext(request.Params?.Context),
                serviceProvider,
                loggerFactory,
                cancellationToken
            )
            ?? McpGatewayMcpServerProtocolMapper.CreateEmptyCompletionResult();
    }

}
