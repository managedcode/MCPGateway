using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayModelRecordTests
{
    [Test]
    public async Task PromptResourceAndEmbeddingRecords_PreserveSuppliedValues()
    {
        var message = new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = "review" },
        };
        var prompt = new McpGatewayPromptResult(
            "local:release_review",
            "local",
            McpGatewaySourceKind.Local,
            "release_review",
            new GetPromptResult
            {
                Description = "Builds a release review prompt.",
                Messages = [message],
            }
        );
        var embedding = new McpGatewayToolEmbedding(
            "local:lookup",
            "local",
            "lookup",
            "hash-1",
            "generator-1",
            [0.1f, 0.2f]
        );
        var contents = new List<ResourceContents>
        {
            new TextResourceContents
            {
                Uri = "docs://release-review",
                MimeType = "text/plain",
                Text = "review",
            },
        };
        var resource = new McpGatewayResourceResult(
            "local",
            McpGatewaySourceKind.Local,
            "docs://release-review",
            contents
        );

        await Assert.That(prompt.PromptId).IsEqualTo("local:release_review");
        await Assert.That(prompt.SourceId).IsEqualTo("local");
        await Assert.That(prompt.SourceKind).IsEqualTo(McpGatewaySourceKind.Local);
        await Assert.That(prompt.PromptName).IsEqualTo("release_review");
        await Assert.That(prompt.Description).IsEqualTo("Builds a release review prompt.");
        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(prompt.Messages[0]).IsEqualTo(message);
        await Assert.That(embedding.ToolId).IsEqualTo("local:lookup");
        await Assert.That(embedding.SourceId).IsEqualTo("local");
        await Assert.That(embedding.ToolName).IsEqualTo("lookup");
        await Assert.That(embedding.DocumentHash).IsEqualTo("hash-1");
        await Assert.That(embedding.Vector.Length).IsEqualTo(2);
        await Assert.That(embedding.EmbeddingGeneratorFingerprint).IsEqualTo("generator-1");
        await Assert.That(resource.SourceId).IsEqualTo("local");
        await Assert.That(resource.SourceKind).IsEqualTo(McpGatewaySourceKind.Local);
        await Assert.That(resource.ResourceUri).IsEqualTo("docs://release-review");
        await Assert.That(ReferenceEquals(resource.Contents, contents)).IsTrue();
        await Assert.That(resource.Contents[0]).IsTypeOf<TextResourceContents>();
    }
}
