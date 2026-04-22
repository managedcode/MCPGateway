namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayModelRecordTests
{
    [Test]
    public async Task PromptAndEmbeddingRecords_PreserveSuppliedValues()
    {
        var prompt = new McpGatewayPromptResult(
            "local:release_review",
            "local",
            McpGatewaySourceKind.Local,
            "release_review",
            "Builds a release review prompt.",
            [new McpGatewayPromptMessage("user", null, "review")]
        );
        var embedding = new McpGatewayToolEmbedding(
            "local:lookup",
            "local",
            "lookup",
            "hash-1",
            "generator-1",
            [0.1f, 0.2f]
        );

        await Assert.That(prompt.PromptId).IsEqualTo("local:release_review");
        await Assert.That(prompt.Messages.Count).IsEqualTo(1);
        await Assert.That(embedding.Vector.Length).IsEqualTo(2);
        await Assert.That(embedding.EmbeddingGeneratorFingerprint).IsEqualTo("generator-1");
    }
}
