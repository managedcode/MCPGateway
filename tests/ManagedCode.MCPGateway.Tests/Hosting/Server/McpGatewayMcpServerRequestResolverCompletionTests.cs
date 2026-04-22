#pragma warning disable MCPEXP001

using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed partial class McpGatewayMcpServerRequestResolverTests
{
    [Test]
    public async Task ResolveCompletionAsync_RewritesPromptAndResourceTemplateReferences()
    {
        var alphaRegistration = new TestRegistration("alpha");
        var resolver = CreateResolver(
            promptDescriptors:
            [
                CreatePromptDescriptor("alpha:release_review", "alpha", "release_review"),
            ],
            resourceDescriptors:
            [
                CreateResourceDescriptor("alpha", "overview", "docs://overview"),
            ],
            templateDescriptors:
            [
                CreateTemplateDescriptor("alpha", "issue_detail", "docs://issues/{id}"),
            ],
            registrations: [alphaRegistration]
        );

        var promptCompletion = await resolver.ResolveCompletionAsync(
            new PromptReference { Name = "alpha:release_review", Title = "Release review" },
            CancellationToken.None
        );
        var resourceCompletion = await resolver.ResolveCompletionAsync(
            new ResourceTemplateReference
            {
                Uri = McpGatewayResourceUriCodec.ToGatewayUri("alpha", "docs://issues/{id}"),
            },
            CancellationToken.None
        );
        var unknown = await resolver.ResolveCompletionAsync(
            new PromptReference { Name = "missing" },
            CancellationToken.None
        );
        var unsupported = await resolver.ResolveCompletionAsync(
            new ResourceTemplateReference { Uri = " " },
            CancellationToken.None
        );

        await Assert.That(promptCompletion).IsNotNull();
        await Assert.That(promptCompletion!.UpstreamReference).IsTypeOf<PromptReference>();
        await Assert
            .That(((PromptReference)promptCompletion.UpstreamReference).Name)
            .IsEqualTo("release_review");
        await Assert.That(resourceCompletion).IsNotNull();
        await Assert.That(resourceCompletion!.UpstreamReference).IsTypeOf<ResourceTemplateReference>();
        await Assert
            .That(((ResourceTemplateReference)resourceCompletion.UpstreamReference).Uri)
            .IsEqualTo("docs://issues/{id}");
        await Assert.That(unknown).IsNull();
        await Assert.That(unsupported).IsNull();
    }

    [Test]
    public async Task ResolveCompletionAsync_DetectsAmbiguousResourceReferences()
    {
        var alphaRegistration = new TestRegistration("alpha");
        var betaRegistration = new TestRegistration("beta");
        var resolver = CreateResolver(
            resourceDescriptors:
            [
                CreateResourceDescriptor("alpha", "overview", "docs://overview"),
                CreateResourceDescriptor("beta", "overview", "docs://overview"),
            ],
            templateDescriptors:
            [
                CreateTemplateDescriptor("alpha", "issue_detail", "docs://issues/{id}"),
                CreateTemplateDescriptor("beta", "issue_detail", "docs://issues/{id}"),
            ],
            registrations: [alphaRegistration, betaRegistration]
        );

        var directException = await CaptureAsync(() =>
            resolver.ResolveCompletionAsync(
                new ResourceTemplateReference { Uri = "docs://issues/{id}" },
                CancellationToken.None
            )
        );
        var resourceException = await CaptureAsync(() =>
            resolver.ResolveCompletionAsync(
                new ResourceTemplateReference { Uri = "docs://overview" },
                CancellationToken.None
            )
        );

        await Assert.That(directException).IsNotNull();
        await Assert.That(directException!.Message).Contains("ambiguous");
        await Assert.That(resourceException).IsNotNull();
        await Assert.That(resourceException!.Message).Contains("ambiguous");
    }
}

#pragma warning restore MCPEXP001
