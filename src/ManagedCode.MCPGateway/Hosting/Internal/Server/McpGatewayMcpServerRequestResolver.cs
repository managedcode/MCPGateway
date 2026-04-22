#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayResolvedPromptRequest(
    string SourceId,
    string PromptName,
    McpGatewayToolSourceRegistration Registration
);

internal sealed record McpGatewayResolvedResourceRequest(
    string SourceId,
    string UpstreamUri,
    string ExposedUri,
    bool UseGatewayUri,
    McpGatewayToolSourceRegistration Registration
);

internal sealed record McpGatewayResolvedCompletionRequest(
    Reference UpstreamReference,
    McpGatewayToolSourceRegistration Registration
);

internal sealed record McpGatewayResolvedToolRequest(
    string ToolId,
    string SourceId,
    string ToolName,
    ToolTaskSupport TaskSupport,
    McpGatewayToolSourceRegistration Registration
);

internal sealed class McpGatewayMcpServerRequestResolver(
    IMcpGatewayCatalogSource catalogSource,
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog,
    IMcpGatewayResourceCatalog resourceCatalog,
    ILoggerFactory loggerFactory
)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    public async Task<McpGatewayResolvedToolRequest?> ResolveToolAsync(
        string toolNameOrId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(toolNameOrId))
        {
            return null;
        }

        var requestedToolName = toolNameOrId.Trim();
        var descriptors = await gateway.ListToolsAsync(cancellationToken);

        var exportedMatch = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.ToolId, requestedToolName, StringComparison.Ordinal)
        );
        if (exportedMatch is not null)
        {
            return await CreateResolvedToolRequestAsync(exportedMatch, cancellationToken);
        }

        var toolNameMatches = descriptors
            .Where(candidate =>
                string.Equals(candidate.ToolName, requestedToolName, StringComparison.Ordinal)
            )
            .ToList();

        return toolNameMatches.Count switch
        {
            0 => null,
            1 => await CreateResolvedToolRequestAsync(toolNameMatches[0], cancellationToken),
            _ => throw new McpException(
                $"Tool name '{requestedToolName}' is ambiguous across multiple sources. Use the exported gateway tool id instead."
            ),
        };
    }

    public async Task<IReadOnlyDictionary<string, ToolTaskSupport?>> LoadToolTaskSupportsAsync(
        CancellationToken cancellationToken
    )
    {
        var descriptors = await gateway.ListToolsAsync(cancellationToken);
        var supports = new Dictionary<string, ToolTaskSupport?>(
            descriptors.Count,
            StringComparer.Ordinal
        );

        foreach (var descriptor in descriptors)
        {
            var registration = FindRegistration(descriptor.SourceId);
            if (registration is null)
            {
                continue;
            }

            var loadedTool = await registration.GetToolAsync(
                descriptor.ToolName,
                _loggerFactory,
                cancellationToken
            );

            supports[descriptor.ToolId] = loadedTool?.TaskSupport;
        }

        return supports;
    }

    public async Task<McpGatewayResolvedPromptRequest?> ResolvePromptAsync(
        string promptNameOrId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(promptNameOrId))
        {
            return null;
        }

        var requestedPromptName = promptNameOrId.Trim();
        var descriptors = await promptCatalog.ListPromptsAsync(cancellationToken);

        var exportedMatch = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.PromptId, requestedPromptName, StringComparison.Ordinal)
        );
        if (exportedMatch is not null)
        {
            return CreateResolvedPromptRequest(exportedMatch.SourceId, exportedMatch.PromptName);
        }

        var promptNameMatches = descriptors
            .Where(candidate =>
                string.Equals(candidate.PromptName, requestedPromptName, StringComparison.Ordinal)
            )
            .ToList();

        return promptNameMatches.Count switch
        {
            0 => null,
            1 => CreateResolvedPromptRequest(
                promptNameMatches[0].SourceId,
                promptNameMatches[0].PromptName
            ),
            _ => throw new McpException(
                $"Prompt name '{requestedPromptName}' is ambiguous across multiple sources. Use the exported gateway prompt name instead."
            ),
        };
    }

    public async Task<McpGatewayResolvedResourceRequest?> ResolveResourceAsync(
        string requestedUri,
        CancellationToken cancellationToken
    )
    {
        if (TryResolveGatewayResourceUri(requestedUri, out var resolvedGatewayRequest))
        {
            return resolvedGatewayRequest;
        }

        var resources = await resourceCatalog.ListResourcesAsync(cancellationToken);
        var matches = resources
            .Where(descriptor =>
                string.Equals(descriptor.ResourceUri, requestedUri, StringComparison.Ordinal)
            )
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => CreateResolvedResourceRequest(
                matches[0].SourceId,
                matches[0].ResourceUri,
                requestedUri,
                useGatewayUri: false
            ),
            _ => throw new McpException(
                $"Resource URI '{requestedUri}' is ambiguous across multiple sources. Use the exported gateway URI instead."
            ),
        };
    }

    public async Task<McpGatewayResolvedCompletionRequest?> ResolveCompletionAsync(
        Reference reference,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reference);

        switch (reference)
        {
            case PromptReference promptReference
                when !string.IsNullOrWhiteSpace(promptReference.Name):
            {
                var resolvedPrompt = await ResolvePromptAsync(promptReference.Name, cancellationToken);
                if (resolvedPrompt is null)
                {
                    return null;
                }

                return new McpGatewayResolvedCompletionRequest(
                    new PromptReference
                    {
                        Name = resolvedPrompt.PromptName,
                        Title = promptReference.Title,
                    },
                    resolvedPrompt.Registration
                );
            }
            case ResourceTemplateReference resourceReference
                when !string.IsNullOrWhiteSpace(resourceReference.Uri):
            {
                var resolvedResource = await ResolveResourceReferenceAsync(
                    resourceReference.Uri,
                    cancellationToken
                );
                if (resolvedResource is null)
                {
                    return null;
                }

                return new McpGatewayResolvedCompletionRequest(
                    new ResourceTemplateReference
                    {
                        Uri = resolvedResource.UpstreamUri,
                    },
                    resolvedResource.Registration
                );
            }
            default:
                return null;
        }
    }

    private bool TryResolveGatewayResourceUri(
        string requestedUri,
        out McpGatewayResolvedResourceRequest? resolvedRequest
    )
    {
        resolvedRequest = null;

        if (
            !McpGatewayResourceUriCodec.TryDecodeGatewayUri(
                requestedUri,
                out var sourceId,
                out var upstreamUri
            )
        )
        {
            return false;
        }

        resolvedRequest = CreateResolvedResourceRequest(
            sourceId,
            upstreamUri,
            requestedUri,
            useGatewayUri: true
        );
        return true;
    }

    private async Task<McpGatewayResolvedResourceRequest?> ResolveResourceReferenceAsync(
        string requestedUri,
        CancellationToken cancellationToken
    )
    {
        if (TryResolveGatewayResourceUri(requestedUri, out var resolvedGatewayRequest))
        {
            return resolvedGatewayRequest;
        }

        var directResources = await resourceCatalog.ListResourcesAsync(cancellationToken);
        var directMatches = directResources
            .Where(descriptor =>
                string.Equals(descriptor.ResourceUri, requestedUri, StringComparison.Ordinal)
            )
            .Select(descriptor => (SourceId: descriptor.SourceId, Uri: descriptor.ResourceUri))
            .ToList();

        var templateResources = await resourceCatalog.ListResourceTemplatesAsync(cancellationToken);
        var templateMatches = templateResources
            .Where(descriptor =>
                string.Equals(descriptor.UriTemplate, requestedUri, StringComparison.Ordinal)
            )
            .Select(descriptor => (descriptor.SourceId, Uri: descriptor.UriTemplate))
            .ToList();

        var matches = directMatches
            .Concat(templateMatches)
            .Distinct()
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => CreateResolvedResourceRequest(
                matches[0].SourceId,
                matches[0].Uri,
                requestedUri,
                useGatewayUri: false
            ),
            _ => throw new McpException(
                $"Resource reference '{requestedUri}' is ambiguous across multiple sources. Use the exported gateway resource URI instead."
            ),
        };
    }

    private McpGatewayResolvedPromptRequest? CreateResolvedPromptRequest(
        string sourceId,
        string promptName
    )
    {
        var registration = FindRegistration(sourceId);
        return registration is null
            ? null
            : new McpGatewayResolvedPromptRequest(sourceId, promptName, registration);
    }

    private McpGatewayResolvedResourceRequest? CreateResolvedResourceRequest(
        string sourceId,
        string upstreamUri,
        string exposedUri,
        bool useGatewayUri
    )
    {
        var registration = FindRegistration(sourceId);
        return registration is null
            ? null
            : new McpGatewayResolvedResourceRequest(
                sourceId,
                upstreamUri,
                exposedUri,
                useGatewayUri,
                registration
            );
    }

    private async Task<McpGatewayResolvedToolRequest?> CreateResolvedToolRequestAsync(
        McpGatewayToolDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        var registration = FindRegistration(descriptor.SourceId);
        if (registration is null)
        {
            return null;
        }

        var loadedTool = await registration.GetToolAsync(
            descriptor.ToolName,
            _loggerFactory,
            cancellationToken
        );
        var taskSupport = loadedTool?.TaskSupport ?? ToolTaskSupport.Forbidden;

        return new McpGatewayResolvedToolRequest(
            descriptor.ToolId,
            descriptor.SourceId,
            descriptor.ToolName,
            taskSupport,
            registration
        );
    }

    private McpGatewayToolSourceRegistration? FindRegistration(string sourceId)
    {
        var snapshot = catalogSource.CreateSnapshot();
        return snapshot.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
        );
    }
}

#pragma warning restore MCPEXP001
