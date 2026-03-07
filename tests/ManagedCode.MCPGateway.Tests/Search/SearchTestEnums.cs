namespace ManagedCode.MCPGateway.Tests;

internal static class TokenizerSearchTestSupport
{
    public static void UseTokenizerSearch(McpGatewayOptions options)
        => options.SearchStrategy = McpGatewaySearchStrategy.Tokenizer;
}

internal enum WorkItemState
{
    Open,
    Closed,
    Merged
}

internal enum TemperatureUnit
{
    Celsius,
    Fahrenheit
}

internal enum FileIntent
{
    Find,
    Read,
    Write,
    Move,
    List
}

internal enum TicketSeverity
{
    Low,
    Medium,
    High,
    Critical
}

internal enum FinanceIntent
{
    Invoice,
    Refund,
    Exchange,
    Reconciliation,
    Tax
}
