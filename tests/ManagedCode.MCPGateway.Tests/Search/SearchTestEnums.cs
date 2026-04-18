namespace ManagedCode.MCPGateway.Tests;

internal enum WorkItemState
{
    Open,
    Closed,
    Merged,
}

internal enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

internal enum FileIntent
{
    Find,
    Read,
    Write,
    Move,
    List,
}

internal enum TicketSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

internal enum FinanceIntent
{
    Invoice,
    Refund,
    Exchange,
    Reconciliation,
    Tax,
}
