using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerSkillsIntegrationTests
{
#pragma warning disable MCPEXP005
    [Test]
    public async Task WithMcpGatewayCatalog_ComposesSdkSkillsWithAggregatedResources()
    {
        const string skillMarkdown = "---\nname: release-guide\ndescription: Publish a release.\n---\n# Release guide\n";
        const string checklist = "Verify the package.";
        var package = McpServerSkillPackage.Create(
        [
            McpServerSkillFile.FromText("SKILL.md", skillMarkdown),
            McpServerSkillFile.FromText("references/checklist.md", checklist)
        ]);
        await using var upstream = await TestMcpServerHost.StartAsync();
        await using var gateway = await GatewayMcpServerHost.StartAsync(
            options => options.AddMcpClient("source-a", upstream.Client, disposeClient: false),
            services => services.AddMcpServer().WithSkillPackages([package]));

        var client = gateway.Client;
        await Assert.That(client.ServerCapabilities.SupportsSkills()).IsTrue();
        var listed = (await client.ListSkillsAsync()).Single();
        var skill = await client.GetSkillAsync(listed.Uri);
        await Assert.That(skill.Name).IsEqualTo("release-guide");
        await Assert.That(skill.Description).IsEqualTo("Publish a release.");

        var resources = await client.ListResourcesAsync();
        var skillFile = await client.ReadSkillResourceAsync(skill, skill.Uri);
        var checklistUri = resources.Single(resource =>
            resource.Uri.EndsWith("/references/checklist.md", StringComparison.Ordinal)).Uri;
        var checklistFile = await client.ReadSkillResourceAsync(skill, checklistUri);
        await Assert.That(((TextResourceContents)skillFile.Contents.Single()).Text).IsEqualTo(skillMarkdown);
        await Assert.That(((TextResourceContents)checklistFile.Contents.Single()).Text).IsEqualTo(checklist);

        var upstreamArchive = resources.Single(resource => resource.Name == "source-a_repository_archive");
        var archive = await client.ReadResourceAsync(upstreamArchive.Uri);
        await Assert.That(((BlobResourceContents)archive.Contents.Single()).DecodedData.ToArray())
            .IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }
#pragma warning restore MCPEXP005
}
