using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

/// <summary>Stores SDK task state and its gateway authorization binding in the same persistence boundary.</summary>
/// <remarks>Creation must atomically persist both the task and owner before returning. Owner metadata
/// has the same lifetime and cross-process visibility as the task. A host store owns its capacity and
/// retention policy; the gateway does not impose an additional process-local expiry.</remarks>
public interface IMcpGatewayTaskStore
{
    Task<McpTaskInfo> CreateTaskAsync(McpGatewayTaskOwner owner, CancellationToken cancellationToken = default);
    Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default);
    Task<McpGatewayTaskOwner?> GetTaskOwnerAsync(string taskId, CancellationToken cancellationToken = default);
    Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default);
    Task SetFailedAsync(string taskId, JsonElement failure, CancellationToken cancellationToken = default);
    Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default);
    Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken = default);
    Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken = default);
    event Action<InputResponseReceivedEventArgs>? InputResponseReceived;
}
