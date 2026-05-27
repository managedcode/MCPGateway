using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceCatalog(
    IMcpGatewayCatalogSource catalogSource,
    ILoggerFactory loggerFactory
) : IMcpGatewayResourceCatalog
{
    public async Task<IReadOnlyList<McpGatewayResourceDescriptor>> ListResourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = catalogSource.CreateSnapshot();
        var descriptors = new List<McpGatewayResourceDescriptor>();

        foreach (var registration in snapshot.Registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resources = await registration.LoadResourcesAsync(loggerFactory, cancellationToken);
            descriptors.AddRange(resources.Select(resource => BuildDescriptor(registration, resource)));
        }

        return descriptors
            .OrderBy(static descriptor => descriptor.SourceId, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.ResourceUri, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<McpGatewayResourceTemplateDescriptor>> ListResourceTemplatesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = catalogSource.CreateSnapshot();
        var descriptors = new List<McpGatewayResourceTemplateDescriptor>();

        foreach (var registration in snapshot.Registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var templates = await registration.LoadResourceTemplatesAsync(
                loggerFactory,
                cancellationToken
            );
            descriptors.AddRange(
                templates.Select(template => BuildDescriptor(registration, template))
            );
        }

        return descriptors
            .OrderBy(static descriptor => descriptor.SourceId, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.UriTemplate, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<McpGatewayResourceResult?> ReadResourceAsync(
        McpGatewayResourceRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ResourceUri))
        {
            throw new ArgumentException("A resource URI is required.", nameof(request));
        }

        var sourceId = request.SourceId.Trim();
        var resourceUri = request.ResourceUri.Trim();
        var snapshot = catalogSource.CreateSnapshot();
        var registration = snapshot.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
        );
        if (registration is null)
        {
            return null;
        }

        var resourceResult = await registration.ReadResourceAsync(
            resourceUri,
            loggerFactory,
            cancellationToken
        );
        if (resourceResult is null)
        {
            return null;
        }

        return new McpGatewayResourceResult(
            SourceId: registration.SourceId,
            SourceKind: McpGatewaySourceKindMapper.Map(registration.Kind),
            ResourceUri: resourceUri,
            Contents: resourceResult.Contents.ToList()
        );
    }

    private static McpGatewayResourceDescriptor BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        McpGatewayLoadedResource resource
    )
    {
        var protocolResource = McpGatewayProtocolPrimitive.Clone(resource.Resource);
        var resourceUri = protocolResource.Uri?.Trim() ?? string.Empty;
        protocolResource.Uri = resourceUri;
        protocolResource.Name = string.IsNullOrWhiteSpace(protocolResource.Name)
            ? resourceUri
            : protocolResource.Name.Trim();
        protocolResource.Title = string.IsNullOrWhiteSpace(protocolResource.Title)
            ? null
            : protocolResource.Title.Trim();

        return new McpGatewayResourceDescriptor(
            SourceId: registration.SourceId,
            SourceKind: McpGatewaySourceKindMapper.Map(registration.Kind),
            ProtocolResource: protocolResource
        );
    }

    private static McpGatewayResourceTemplateDescriptor BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        McpGatewayLoadedResourceTemplate template
    )
    {
        var protocolTemplate = McpGatewayProtocolPrimitive.Clone(template.ResourceTemplate);
        var uriTemplate = protocolTemplate.UriTemplate?.Trim() ?? string.Empty;
        protocolTemplate.UriTemplate = uriTemplate;
        protocolTemplate.Name = string.IsNullOrWhiteSpace(protocolTemplate.Name)
            ? uriTemplate
            : protocolTemplate.Name.Trim();
        protocolTemplate.Title = string.IsNullOrWhiteSpace(protocolTemplate.Title)
            ? null
            : protocolTemplate.Title.Trim();

        return new McpGatewayResourceTemplateDescriptor(
            SourceId: registration.SourceId,
            SourceKind: McpGatewaySourceKindMapper.Map(registration.Kind),
            ProtocolResourceTemplate: protocolTemplate
        );
    }
}
