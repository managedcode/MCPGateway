using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayInvocationTelemetry
{
    private const string Name = "ManagedCode.MCPGateway";
    private const string OutcomeTag = "mcpgateway.invocation.outcome";
    private const string OperationTag = "mcpgateway.operation";
    private const string InvocationActivityName = "ManagedCode.MCPGateway.Invoke";
    private const string InvocationOperation = "invoke";
    private const string RequestsName = "mcpgateway.invocation.requests";
    private const string DurationName = "mcpgateway.invocation.duration";
    private const string BytesName = "mcpgateway.invocation.delivered_bytes";
    private const string PublicationsName = "mcpgateway.catalog.publications";
    private const string RefreshesName = "mcpgateway.catalog.source_refreshes";
    private const string SourceBytesName = "mcpgateway.invocation.source_payload_bytes";
    private const string ApprovedBytesName = "mcpgateway.invocation.approved_payload_bytes";
    private const string ProjectionsName = "mcpgateway.invocation.projections";
    private const string StoredName = "mcpgateway.invocation.stored_results";
    private const string ResultReadsName = "mcpgateway.result.reads";
    private const string DispatchDurationName = "mcpgateway.invocation.dispatch_duration";
    private const string AdmissionDurationName = "mcpgateway.invocation.admission_duration";
    private static readonly Meter Meter = new(Name);
    private static readonly ActivitySource Activities = new(Name);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(RequestsName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(DurationName, "ms");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>(BytesName, "By");
    private static readonly Counter<long> Publications = Meter.CreateCounter<long>(PublicationsName);
    private static readonly Counter<long> Refreshes = Meter.CreateCounter<long>(RefreshesName);
    private const string RefreshSucceededTag = "mcpgateway.catalog.refresh_succeeded";
    public static void SourceRefresh(bool succeeded) => Refreshes.Add(1, new TagList { { RefreshSucceededTag, succeeded } });
    private static readonly Histogram<long> SourceBytes = Meter.CreateHistogram<long>(SourceBytesName, "By");
    private static readonly Histogram<long> ApprovedBytes = Meter.CreateHistogram<long>(ApprovedBytesName, "By");
    private static readonly Counter<long> Projections = Meter.CreateCounter<long>(ProjectionsName);
    private static readonly Counter<long> Stored = Meter.CreateCounter<long>(StoredName);
    public static bool SourceSizeEnabled => SourceBytes.Enabled;
    public static bool ApprovedSizeEnabled => ApprovedBytes.Enabled;
    public static void SourcePayload(int bytes) => SourceBytes.Record(bytes);
    public static void ApprovedPayload(int bytes) => ApprovedBytes.Record(bytes);
    public static void Projection() => Projections.Add(1);
    public static void StoredResult() => Stored.Add(1);
    private static readonly Counter<long> ResultReads = Meter.CreateCounter<long>(ResultReadsName);
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>(DispatchDurationName, "ms");
    private static readonly Histogram<double> AdmissionDuration = Meter.CreateHistogram<double>(AdmissionDurationName, "ms");
    public static void Dispatch(double milliseconds) => DispatchDuration.Record(milliseconds);
    public static void Admission(double milliseconds) => AdmissionDuration.Record(milliseconds);
    private const string ResultDeliveredTag = "mcpgateway.result.delivered";
    public static void ResultRead(bool delivered) => ResultReads.Add(1, new TagList { { ResultDeliveredTag, delivered } });
    private const string PublishedTag = "mcpgateway.catalog.published";
    public static Activity? Start() => Activities.StartActivity(InvocationActivityName, ActivityKind.Internal);
    public static void Complete(Activity? activity, McpGatewayInvokeResult result, double milliseconds)
    {
        var tags = new TagList { { OutcomeTag, result.Outcome.ToString() }, { OperationTag, InvocationOperation } };
        Requests.Add(1, tags);
        Duration.Record(milliseconds, tags);
        Bytes.Record(result.DeliveredBytes ?? 0, tags);
        activity?.SetTag(OutcomeTag, result.Outcome.ToString());
    }
    public static void Publication(McpGatewayPublicationResult result) => Publications.Add(1,
        new TagList { { PublishedTag, result.IsPublished } });
}
