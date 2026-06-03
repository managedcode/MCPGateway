namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayProtocolNameTests
{
    [TUnit.Core.Test]
    public async Task Normalize_CollapsesUnsafeCharacterRunsAndLowercases()
    {
        var name = McpGatewayProtocolName.Normalize(
            "  Notion Fetch/Page.Details::Lookup  ",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("notion_fetch_page_details_lookup");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_PreservesHyphensUnderscoresAndDigits()
    {
        var name = McpGatewayProtocolName.Normalize(
            "ToolID_2-FETCH",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("toolid_2-fetch");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_AllowsNamesThatStartWithDigits()
    {
        var name = McpGatewayProtocolName.Normalize(
            "123 Tool Search",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("123_tool_search");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_UsesFallbackWhenInputHasNoSafeCharacters()
    {
        var name = McpGatewayProtocolName.Normalize(" 🧨 / : ", "fallback_tool");

        await Assert.That(name).IsEqualTo("fallback_tool");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_DoesNotHashShortUniqueNames()
    {
        var name = McpGatewayProtocolName.Normalize(
            "notion-fetch",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("notion-fetch");
        await Assert.That(HasHashSuffix(name)).IsFalse();
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_HashShortensLongNamesWithReadablePrefixAndMaxLength()
    {
        var name = McpGatewayProtocolName.Normalize(
            string.Concat("archive_lookup_", new string('a', 120)),
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).StartsWith("archive_lookup_");
        await Assert.That(HasHashSuffix(name)).IsTrue();
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task Normalize_HashShorteningIsDeterministic()
    {
        var rawName = string.Concat("notion_fetch_page_details_", new string('b', 120));

        var first = McpGatewayProtocolName.Normalize(rawName, McpGatewayProtocolName.DefaultToolName);
        var second = McpGatewayProtocolName.Normalize(rawName, McpGatewayProtocolName.DefaultToolName);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(HasHashSuffix(first)).IsTrue();
        await AssertMcpSafeNameAsync(first);
    }

    [TUnit.Core.Test]
    public async Task Normalize_HashShorteningSeparatesNamesWithTheSameLongPrefix()
    {
        var commonPrefix = string.Concat("notion_fetch_page_details_", new string('c', 90));

        var first = McpGatewayProtocolName.Normalize(
            string.Concat(commonPrefix, "_first"),
            McpGatewayProtocolName.DefaultToolName
        );
        var second = McpGatewayProtocolName.Normalize(
            string.Concat(commonPrefix, "_second"),
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(first[..55]).IsEqualTo(second[..55]);
        await Assert.That(HasHashSuffix(first)).IsTrue();
        await Assert.That(HasHashSuffix(second)).IsTrue();
        await AssertMcpSafeNameAsync(first);
        await AssertMcpSafeNameAsync(second);
    }

    [TUnit.Core.Test]
    public async Task CreateSourceQualifiedName_NormalizesSourceAndName()
    {
        var name = McpGatewayProtocolName.CreateSourceQualifiedName(
            "Remote:Docs",
            "Search/Repository",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("remote_docs_search_repository");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task CreateSourceQualifiedName_KeepsAlreadySourcePrefixedName()
    {
        var name = McpGatewayProtocolName.CreateSourceQualifiedName(
            "docs",
            "docs_search_repository",
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).IsEqualTo("docs_search_repository");
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task CreateSourceQualifiedName_HashShortensLongCompositeNames()
    {
        var name = McpGatewayProtocolName.CreateSourceQualifiedName(
            "notion-remote-workspace",
            string.Concat("fetch_page_details_", new string('d', 100)),
            McpGatewayProtocolName.DefaultToolName
        );

        await Assert.That(name).StartsWith("notion-remote-workspace_fetch_page");
        await Assert.That(HasHashSuffix(name)).IsTrue();
        await AssertMcpSafeNameAsync(name);
    }

    [TUnit.Core.Test]
    public async Task CreateToolId_ReturnsShortUniqueToolNameWithoutSourcePrefixOrHash()
    {
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolId = McpGatewayProtocolName.CreateToolId(
            "notion",
            "notion-fetch",
            reservedIds
        );

        await Assert.That(toolId).IsEqualTo("notion-fetch");
        await Assert.That(HasHashSuffix(toolId)).IsFalse();
        await AssertMcpSafeNameAsync(toolId);
    }

    [TUnit.Core.Test]
    public async Task CreateToolId_SourceQualifiesCollidingToolNames()
    {
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = McpGatewayProtocolName.CreateToolId(
            "docs",
            "search_repository",
            reservedIds,
            requireSourcePrefix: true
        );
        var second = McpGatewayProtocolName.CreateToolId(
            "ops",
            "search_repository",
            reservedIds,
            requireSourcePrefix: true
        );

        await Assert.That(first).IsEqualTo("docs_search_repository");
        await Assert.That(second).IsEqualTo("ops_search_repository");
        await AssertMcpSafeNameAsync(first);
        await AssertMcpSafeNameAsync(second);
    }

    [TUnit.Core.Test]
    public async Task CreateToolId_UsesHashSuffixForExactDuplicateSourceToolPairs()
    {
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = McpGatewayProtocolName.CreateToolId(
            "local",
            "search_repository",
            reservedIds,
            requireSourcePrefix: true
        );
        var second = McpGatewayProtocolName.CreateToolId(
            "local",
            "search_repository",
            reservedIds,
            requireSourcePrefix: true
        );

        await Assert.That(first).IsEqualTo("local_search_repository");
        await Assert.That(second).StartsWith("local_search_repository_");
        await Assert.That(HasHashSuffix(second)).IsTrue();
        await AssertMcpSafeNameAsync(first);
        await AssertMcpSafeNameAsync(second);
    }

    [TUnit.Core.Test]
    public async Task CreateToolId_TreatsReservedIdsCaseInsensitively()
    {
        var reservedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOTION-FETCH",
        };

        var toolId = McpGatewayProtocolName.CreateToolId(
            "Remote Source",
            "notion-fetch",
            reservedIds
        );

        await Assert.That(toolId).IsEqualTo("remote_source_notion-fetch");
        await AssertMcpSafeNameAsync(toolId);
    }

    [TUnit.Core.Test]
    public async Task AppendSuffix_KeepsCollisionSuffixInsideMaxLength()
    {
        var name = McpGatewayProtocolName.AppendSuffix(
            string.Concat("notion_fetch_page_details_", new string('e', 100)),
            "_12"
        );

        await Assert.That(name).EndsWith("_12");
        await AssertMcpSafeNameAsync(name);
    }

    private static async Task AssertMcpSafeNameAsync(string name)
    {
        await Assert.That(name.Length).IsGreaterThan(0);
        await Assert.That(name.Length).IsLessThanOrEqualTo(McpGatewayProtocolName.MaxNameLength);
        await Assert
            .That(
                name.All(static character =>
                    character is >= 'a' and <= 'z'
                    || character is >= '0' and <= '9'
                    || character is '_' or '-'
                )
            )
            .IsTrue();
    }

    private static bool HasHashSuffix(string name)
    {
        if (name.Length < 9 || name[^9] != '_')
        {
            return false;
        }

        return name[^8..].All(static character =>
            character is >= 'a' and <= 'f' || character is >= '0' and <= '9'
        );
    }
}
