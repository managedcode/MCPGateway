using System.Collections.Frozen;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static readonly char[] TokenSeparators =
    [
        ' ',
        '\t',
        '\r',
        '\n',
        '_',
        '-',
        '.',
        ',',
        ';',
        ':',
        '/',
        '\\',
        '(',
        ')',
        '[',
        ']',
        '{',
        '}',
        '"',
        '\'',
        '@',
        '?',
        '!',
    ];

    private static readonly FrozenSet<string> IgnoredSearchTerms = new[]
    {
        "a",
        "an",
        "and",
        "again",
        "any",
        "for",
        "just",
        "me",
        "need",
        "now",
        "please",
        "plz",
        "really",
        "something",
        "stuff",
        "that",
        "the",
        "thing",
        "this",
        "to",
        "active",
        "browser",
        "browsing",
        "context",
        "dashboard",
        "dashboards",
        "false",
        "filter",
        "filters",
        "intent",
        "mode",
        "page",
        "section",
        "signal",
        "signals",
        "summary",
        "true",
        "user",
        "with",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> GraphDiscoveryTerms = new[]
    {
        GraphOperationTermSearch,
        GraphOperationTermFind,
        GraphOperationTermList,
        GraphOperationTermQuery,
        GraphOperationTermDiscover,
        GraphOperationTermBrowse,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> GraphInspectionTerms = new[]
    {
        GraphOperationTermGet,
        GraphOperationTermRead,
        GraphOperationTermLookup,
        GraphOperationTermDetail,
        GraphOperationTermDetails,
        GraphOperationTermFetch,
        GraphOperationTermShow,
        GraphOperationTermInspect,
        GraphOperationTermStatus,
        GraphOperationTermRetrieve,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> GraphActionTerms = new[]
    {
        GraphOperationTermCreate,
        GraphOperationTermUpdate,
        GraphOperationTermDelete,
        GraphOperationTermRemove,
        GraphOperationTermAdd,
        GraphOperationTermSet,
        GraphOperationTermSend,
        GraphOperationTermPost,
        GraphOperationTermWrite,
        GraphOperationTermInvoke,
        GraphOperationTermRun,
        GraphOperationTermExecute,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> GraphGenericTerms = new[]
    {
        GraphGenericToolTerm,
        GraphGenericToolsTerm,
        GraphGenericMcpTerm,
        GraphGenericGatewayTerm,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
