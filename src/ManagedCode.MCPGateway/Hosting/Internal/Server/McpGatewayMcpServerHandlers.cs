#pragma warning disable MCPEXP001

using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayMcpServerHandlers(
    McpGatewayMcpServerBindingManager bindingManager,
    McpGatewayMcpServerRequestResolver requestResolver,
    McpGatewayResourceSubscriptionManager subscriptionManager,
    McpGatewayPromptListNotificationManager promptNotificationManager,
    McpGatewayMcpServerTaskStore taskStore,
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory
)
{
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

        var descriptors = await binding.Binding.Gateway.ListToolsAsync(cancellationToken);
        var taskSupports = await requestResolver.LoadToolTaskSupportsAsync(
            binding.Binding,
            cancellationToken
        );
        return new ListToolsResult
        {
            Tools = descriptors
                .Select(descriptor =>
                    McpGatewayMcpServerProtocolMapper.ToProtocolTool(
                        descriptor,
                        taskSupports.TryGetValue(descriptor.ToolId, out var taskSupport)
                            ? taskSupport
                            : null
                    )
                )
                .ToList(),
        };
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

        var resolvedTool = await requestResolver.ResolveToolAsync(
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
        if (request.Params?.Task is not null)
        {
            return await taskStore.CreateToolTaskAsync(
                request,
                resolvedTool,
                arguments,
                cancellationToken
            );
        }

        if (resolvedTool.TaskSupport == ToolTaskSupport.Required)
        {
            throw new McpException($"Tool '{resolvedTool.ToolId}' requires task augmentation.");
        }

        var invokeResult = await binding.Binding.Gateway.InvokeAsync(
            new McpGatewayInvokeRequest(
                ToolId: resolvedTool.ToolId,
                Arguments: arguments
            ),
            cancellationToken
        );

        return McpGatewayMcpServerProtocolMapper.ToProtocolToolResult(invokeResult);
    }

    public async ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        await promptNotificationManager.RegisterDownstreamServerAsync(
            request.Services,
            request.Server,
            cancellationToken
        );

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
        await promptNotificationManager.RegisterDownstreamServerAsync(
            request.Services,
            request.Server,
            cancellationToken
        );

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
        await promptNotificationManager.RegisterDownstreamServerAsync(
            request.Services,
            request.Server,
            cancellationToken
        );

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

    public async ValueTask<EmptyResult> SubscribeToResourceAsync(
        RequestContext<SubscribeRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var requestedUri = request.Params?.Uri?.Trim();
        if (string.IsNullOrWhiteSpace(requestedUri))
        {
            throw new McpException(McpGatewayMcpProtocolConstants.InvalidResourceUriMessage);
        }

        await subscriptionManager.SubscribeAsync(
            request.Services,
            request.Server,
            requestedUri,
            cancellationToken
        );

        return new EmptyResult();
    }

    public async ValueTask<EmptyResult> UnsubscribeFromResourceAsync(
        RequestContext<UnsubscribeRequestParams> request,
        CancellationToken cancellationToken
    )
    {
        var requestedUri = request.Params?.Uri?.Trim();
        if (string.IsNullOrWhiteSpace(requestedUri))
        {
            throw new McpException(McpGatewayMcpProtocolConstants.InvalidResourceUriMessage);
        }

        await subscriptionManager.UnsubscribeAsync(request.Server, requestedUri, cancellationToken);
        return new EmptyResult();
    }
}

#pragma warning restore MCPEXP001
