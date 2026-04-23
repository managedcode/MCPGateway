#pragma warning disable MCPEXP001

using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayResolvedPromptRequest(
    string SourceId,
    string PromptName,
    IMcpGatewayServerSource Source
);

internal sealed record McpGatewayResolvedResourceRequest(
    string SourceId,
    string UpstreamUri,
    string ExposedUri,
    bool UseGatewayUri,
    IMcpGatewayServerSource Source
);

internal sealed record McpGatewayResolvedCompletionRequest(
    Reference UpstreamReference,
    IMcpGatewayServerSource Source
);

internal sealed record McpGatewayResolvedToolRequest(
    string ToolId,
    string SourceId,
    string ToolName,
    ToolTaskSupport TaskSupport,
    IMcpGatewayServerSource Source
);

internal sealed class McpGatewayMcpServerRequestResolver(ILoggerFactory loggerFactory)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    public async Task<McpGatewayResolvedToolRequest?> ResolveToolAsync(
        IMcpGatewayServerBinding binding,
        string toolNameOrId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (string.IsNullOrWhiteSpace(toolNameOrId))
        {
            return null;
        }

        var requestedToolName = toolNameOrId.Trim();
        var descriptors = await binding.Gateway.ListToolsAsync(cancellationToken);

        var exportedMatch = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.ToolId, requestedToolName, StringComparison.Ordinal)
        );
        if (exportedMatch is not null)
        {
            return await CreateResolvedToolRequestAsync(binding, exportedMatch, cancellationToken);
        }

        var toolNameMatches = descriptors
            .Where(candidate =>
            string.Equals(candidate.ToolName, requestedToolName, StringComparison.Ordinal)
            )
            .ToList();

        return toolNameMatches.Count switch
        {
            0 => null,
            1 => await CreateResolvedToolRequestAsync(binding, toolNameMatches[0], cancellationToken),
            _ => throw new McpException(
                $"Tool name '{requestedToolName}' is ambiguous across multiple sources. Use the exported gateway tool id instead."
            ),
        };
    }

    public async Task<IReadOnlyDictionary<string, ToolTaskSupport?>> LoadToolTaskSupportsAsync(
        IMcpGatewayServerBinding binding,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        var descriptors = await binding.Gateway.ListToolsAsync(cancellationToken);
        var supports = new Dictionary<string, ToolTaskSupport?>(
            descriptors.Count,
            StringComparer.Ordinal
        );

        foreach (var descriptor in descriptors)
        {
            var source = await FindSourceAsync(binding, descriptor.SourceId, cancellationToken);
            if (source is null)
            {
                continue;
            }

            var taskSupport = await source.GetToolTaskSupportAsync(
                descriptor.ToolName,
                _loggerFactory,
                cancellationToken
            );

            supports[descriptor.ToolId] = taskSupport;
        }

        return supports;
    }

    public static async Task<McpGatewayResolvedPromptRequest?> ResolvePromptAsync(
        IMcpGatewayServerBinding binding,
        string promptNameOrId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (string.IsNullOrWhiteSpace(promptNameOrId))
        {
            return null;
        }

        var requestedPromptName = promptNameOrId.Trim();
        var descriptors = await binding.PromptCatalog.ListPromptsAsync(cancellationToken);

        var exportedMatch = descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.PromptId, requestedPromptName, StringComparison.Ordinal)
        );
        if (exportedMatch is not null)
        {
            return await CreateResolvedPromptRequestAsync(
                binding,
                exportedMatch.SourceId,
                exportedMatch.PromptName,
                cancellationToken
            );
        }

        var promptNameMatches = descriptors
            .Where(candidate =>
                string.Equals(candidate.PromptName, requestedPromptName, StringComparison.Ordinal)
            )
            .ToList();

        return promptNameMatches.Count switch
        {
            0 => null,
            1 => await CreateResolvedPromptRequestAsync(
                binding,
                promptNameMatches[0].SourceId,
                promptNameMatches[0].PromptName,
                cancellationToken
            ),
            _ => throw new McpException(
                $"Prompt name '{requestedPromptName}' is ambiguous across multiple sources. Use the exported gateway prompt name instead."
            ),
        };
    }

    public static async Task<McpGatewayResolvedResourceRequest?> ResolveResourceAsync(
        IMcpGatewayServerBinding binding,
        string requestedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(binding);

        var resolvedGatewayRequest = await TryResolveGatewayResourceUriAsync(
            binding,
            requestedUri,
            cancellationToken
        );
        if (resolvedGatewayRequest is not null)
        {
            return resolvedGatewayRequest;
        }

        var resources = await binding.ResourceCatalog.ListResourcesAsync(cancellationToken);
        var matches = resources
            .Where(descriptor =>
                string.Equals(descriptor.ResourceUri, requestedUri, StringComparison.Ordinal)
            )
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => await CreateResolvedResourceRequestAsync(
                binding,
                matches[0].SourceId,
                matches[0].ResourceUri,
                requestedUri,
                useGatewayUri: false,
                cancellationToken
            ),
            _ => throw new McpException(
                $"Resource URI '{requestedUri}' is ambiguous across multiple sources. Use the exported gateway URI instead."
            ),
        };
    }

    public static async Task<McpGatewayResolvedCompletionRequest?> ResolveCompletionAsync(
        IMcpGatewayServerBinding binding,
        Reference reference,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(binding);

        switch (reference)
        {
            case PromptReference promptReference
                when !string.IsNullOrWhiteSpace(promptReference.Name):
            {
                var resolvedPrompt = await ResolvePromptAsync(
                    binding,
                    promptReference.Name,
                    cancellationToken
                );
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
                    resolvedPrompt.Source
                );
            }
            case ResourceTemplateReference resourceReference
                when !string.IsNullOrWhiteSpace(resourceReference.Uri):
            {
                var resolvedResource = await ResolveResourceReferenceAsync(
                    binding,
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
                    resolvedResource.Source
                );
            }
            default:
                return null;
        }
    }

    private static async Task<McpGatewayResolvedResourceRequest?> TryResolveGatewayResourceUriAsync(
        IMcpGatewayServerBinding binding,
        string requestedUri,
        CancellationToken cancellationToken
    )
    {
        if (
            !McpGatewayResourceUriCodec.TryDecodeGatewayUri(
                requestedUri,
                out var sourceId,
                out var upstreamUri
            )
        )
        {
            return null;
        }

        return await CreateResolvedResourceRequestAsync(
            binding,
            sourceId,
            upstreamUri,
            requestedUri,
            useGatewayUri: true,
            cancellationToken
        );
    }

    private static async Task<McpGatewayResolvedResourceRequest?> ResolveResourceReferenceAsync(
        IMcpGatewayServerBinding binding,
        string requestedUri,
        CancellationToken cancellationToken
    )
    {
        var resolvedGatewayRequest = await TryResolveGatewayResourceUriAsync(
            binding,
            requestedUri,
            cancellationToken
        );
        if (resolvedGatewayRequest is not null)
        {
            return resolvedGatewayRequest;
        }

        var directResources = await binding.ResourceCatalog.ListResourcesAsync(cancellationToken);
        var directMatches = directResources
            .Where(descriptor =>
                string.Equals(descriptor.ResourceUri, requestedUri, StringComparison.Ordinal)
            )
            .Select(descriptor => (SourceId: descriptor.SourceId, Uri: descriptor.ResourceUri))
            .ToList();

        var templateResources = await binding.ResourceCatalog.ListResourceTemplatesAsync(cancellationToken);
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
            1 => await CreateResolvedResourceRequestAsync(
                binding,
                matches[0].SourceId,
                matches[0].Uri,
                requestedUri,
                useGatewayUri: false,
                cancellationToken
            ),
            _ => throw new McpException(
                $"Resource reference '{requestedUri}' is ambiguous across multiple sources. Use the exported gateway resource URI instead."
            ),
        };
    }

    private static async Task<McpGatewayResolvedPromptRequest?> CreateResolvedPromptRequestAsync(
        IMcpGatewayServerBinding binding,
        string sourceId,
        string promptName,
        CancellationToken cancellationToken
    )
    {
        var source = await FindSourceAsync(binding, sourceId, cancellationToken);
        return source is null
            ? null
            : new McpGatewayResolvedPromptRequest(sourceId, promptName, source);
    }

    private static async Task<McpGatewayResolvedResourceRequest?> CreateResolvedResourceRequestAsync(
        IMcpGatewayServerBinding binding,
        string sourceId,
        string upstreamUri,
        string exposedUri,
        bool useGatewayUri,
        CancellationToken cancellationToken
    )
    {
        var source = await FindSourceAsync(binding, sourceId, cancellationToken);
        return source is null
            ? null
            : new McpGatewayResolvedResourceRequest(
                sourceId,
                upstreamUri,
                exposedUri,
                useGatewayUri,
                source
            );
    }

    private async Task<McpGatewayResolvedToolRequest?> CreateResolvedToolRequestAsync(
        IMcpGatewayServerBinding binding,
        McpGatewayToolDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        var source = await FindSourceAsync(binding, descriptor.SourceId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        var taskSupport =
            await source.GetToolTaskSupportAsync(
                descriptor.ToolName,
                _loggerFactory,
                cancellationToken
            ) ?? ToolTaskSupport.Forbidden;

        return new McpGatewayResolvedToolRequest(
            descriptor.ToolId,
            descriptor.SourceId,
            descriptor.ToolName,
            taskSupport,
            source
        );
    }

    private static async Task<IMcpGatewayServerSource?> FindSourceAsync(
        IMcpGatewayServerBinding binding,
        string sourceId,
        CancellationToken cancellationToken
    )
    {
        var sources = await binding.ListSourcesAsync(cancellationToken);
        return sources.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
        );
    }
}

#pragma warning restore MCPEXP001
