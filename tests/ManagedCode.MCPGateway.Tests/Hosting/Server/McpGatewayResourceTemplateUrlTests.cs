using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceTemplateUrlTests
{
    [Test]
    public async Task OpaqueResourceUri_ReadsThroughGateway()
    {
        var catalog = new RecordingResourceCatalog(
            [
                new McpGatewayResourceDescriptor(
                    "source-a",
                    McpGatewaySourceKind.Local,
                    new Resource
                    {
                        Name = "file_detail",
                        Title = "File detail",
                        Uri = "/files/readme.md",
                        Description = "Reads a file resource.",
                        MimeType = "text/plain",
                    }
                ),
            ],
            []
        );
        var source = new PassiveSource("source-a");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(
                    new SingleSourceServerBindingResolver(source, catalog)
                )
        );

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == "source-a:file_detail"
        );
        var readResult = await gatewayServer.Client.ReadResourceAsync(resource.Uri);
        var content = (TextResourceContents)readResult.Contents.Single();
        var decoded = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            content.Uri,
            out var sourceId,
            out var upstreamUri
        );

        await Assert.That(resource.Uri).StartsWith("mcpgw-");
        await Assert.That(catalog.LastRequest?.SourceId).IsEqualTo("source-a");
        await Assert.That(catalog.LastRequest?.ResourceUri).IsEqualTo("/files/readme.md");
        await Assert.That(decoded).IsTrue();
        await Assert.That(sourceId).IsEqualTo("source-a");
        await Assert.That(upstreamUri).IsEqualTo("/files/readme.md");
        await Assert.That(content.Text).IsEqualTo("read:/files/readme.md");
    }

    [Test]
    public async Task OpaqueResourceTemplateUri_ExpandsAndReadsThroughGateway()
    {
        var catalog = new RecordingResourceCatalog(
            [],
            [
                new McpGatewayResourceTemplateDescriptor(
                    "source-a",
                    McpGatewaySourceKind.Local,
                    new ResourceTemplate
                    {
                        Name = "file_detail",
                        Title = "File detail",
                        UriTemplate = "/files/{path}",
                        Description = "Reads a file resource.",
                        MimeType = "text/plain",
                    }
                ),
            ]
        );
        var source = new PassiveSource("source-a");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(
                    new SingleSourceServerBindingResolver(source, catalog)
                )
        );

        var template = (await gatewayServer.Client.ListResourceTemplatesAsync()).Single(
            static candidate => candidate.Name == "source-a:file_detail"
        );

        await Assert.That(template.UriTemplate).StartsWith("mcpgw-");
        await Assert.That(template.UriTemplate).Contains("{path}");

        var readResult = await gatewayServer.Client.ReadResourceAsync(
            template.UriTemplate,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = "readme.md" }
        );

        var content = (TextResourceContents)readResult.Contents.Single();
        var decoded = McpGatewayResourceUriCodec.TryDecodeGatewayUri(
            content.Uri,
            out var sourceId,
            out var upstreamUri
        );

        await Assert.That(catalog.LastRequest?.SourceId).IsEqualTo("source-a");
        await Assert.That(catalog.LastRequest?.ResourceUri).IsEqualTo("/files/readme.md");
        await Assert.That(decoded).IsTrue();
        await Assert.That(sourceId).IsEqualTo("source-a");
        await Assert.That(upstreamUri).IsEqualTo("/files/readme.md");
        await Assert.That(content.Text).IsEqualTo("read:/files/readme.md");
    }

    private sealed class PassiveSource(string sourceId) : TestMcpGatewayServerSource(sourceId);

    private sealed class RecordingResourceCatalog(
        IReadOnlyList<McpGatewayResourceDescriptor> resources,
        IReadOnlyList<McpGatewayResourceTemplateDescriptor> templates
    ) : IMcpGatewayResourceCatalog
    {
        public McpGatewayResourceRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<McpGatewayResourceDescriptor>> ListResourcesAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(resources);
        }

        public Task<IReadOnlyList<McpGatewayResourceTemplateDescriptor>> ListResourceTemplatesAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(templates);
        }

        public Task<McpGatewayResourceResult?> ReadResourceAsync(
            McpGatewayResourceRequest request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;

            return Task.FromResult<McpGatewayResourceResult?>(
                new McpGatewayResourceResult(
                    request.SourceId,
                    McpGatewaySourceKind.Local,
                    request.ResourceUri,
                    [
                        new TextResourceContents
                        {
                            Uri = request.ResourceUri,
                            MimeType = "text/plain",
                            Text = $"read:{request.ResourceUri}",
                        },
                    ]
                )
            );
        }
    }
}
