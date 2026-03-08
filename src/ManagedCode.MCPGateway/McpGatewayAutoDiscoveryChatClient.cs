using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayAutoDiscoveryChatClient : IChatClient
{
    private readonly IChatClient _innerClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _functionInvocationServices;
    private readonly McpGatewayToolSet _toolSet;
    private readonly McpGatewayAutoDiscoveryOptions _options;

    public McpGatewayAutoDiscoveryChatClient(
        IChatClient innerClient,
        McpGatewayToolSet toolSet,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? functionInvocationServices = null,
        McpGatewayAutoDiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(innerClient);
        ArgumentNullException.ThrowIfNull(toolSet);

        _innerClient = innerClient;
        _toolSet = toolSet;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _functionInvocationServices = functionInvocationServices ?? EmptyServiceProvider.Instance;
        _options = options?.Clone() ?? new McpGatewayAutoDiscoveryOptions();
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var functionInvokingChatClient = CreateFunctionInvokingChatClient();
        return await functionInvokingChatClient.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var functionInvokingChatClient = CreateFunctionInvokingChatClient();

        await foreach (var update in functionInvokingChatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return _innerClient.GetService(serviceType, serviceKey);
    }

    public void Dispose() => _innerClient.Dispose();

    private FunctionInvokingChatClient CreateFunctionInvokingChatClient()
    {
        FunctionInvokingChatClient? functionInvokingChatClient = null;

        var requestClient = new AutoDiscoveryRequestChatClient(
            _innerClient,
            _toolSet,
            _options,
            gatewayTools =>
            {
                if (functionInvokingChatClient is not null)
                {
                    functionInvokingChatClient.AdditionalTools = gatewayTools.ToList();
                }
            });

        functionInvokingChatClient = new FunctionInvokingChatClient(
            requestClient,
            _loggerFactory,
            _functionInvocationServices);

        return functionInvokingChatClient;
    }

    private sealed class AutoDiscoveryRequestChatClient(
        IChatClient innerClient,
        McpGatewayToolSet toolSet,
        McpGatewayAutoDiscoveryOptions options,
        Action<IReadOnlyList<AITool>> updateAdditionalTools) : IChatClient
    {
        private readonly IChatClient _innerClient = innerClient;
        private readonly McpGatewayToolSet _toolSet = toolSet;
        private readonly McpGatewayAutoDiscoveryOptions _options = options;
        private readonly Action<IReadOnlyList<AITool>> _updateAdditionalTools = updateAdditionalTools;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            var gatewayTools = CreateGatewayTools(messageList);
            _updateAdditionalTools(gatewayTools);

            return _innerClient.GetResponseAsync(
                messageList,
                CreateOptions(options, gatewayTools),
                cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            var gatewayTools = CreateGatewayTools(messageList);
            _updateAdditionalTools(gatewayTools);

            return _innerClient.GetStreamingResponseAsync(
                messageList,
                CreateOptions(options, gatewayTools),
                cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return _innerClient.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
        }

        private IReadOnlyList<AITool> CreateGatewayTools(IReadOnlyList<ChatMessage> messages)
        {
            var gatewayTools = _toolSet
                .CreateTools(_options.SearchToolName, _options.InvokeToolName)
                .ToList();

            var latestSearchResult = FindLatestSearchResult(messages);
            if (latestSearchResult?.Matches.Count is not > 0)
            {
                return gatewayTools;
            }

            var reservedToolNames = new HashSet<string>(
                gatewayTools.Select(static tool => tool.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var discoveredTool in _toolSet.CreateDiscoveredTools(
                         latestSearchResult.Matches,
                         reservedToolNames,
                         Math.Max(0, _options.MaxDiscoveredTools)))
            {
                gatewayTools.Add(discoveredTool);
            }

            return gatewayTools;
        }

        private static ChatOptions CreateOptions(ChatOptions? options, IReadOnlyList<AITool> gatewayTools)
        {
            var effectiveOptions = options?.Clone() ?? new ChatOptions();
            var effectiveTools = effectiveOptions.Tools is { Count: > 0 }
                ? effectiveOptions.Tools.ToList()
                : [];

            foreach (var gatewayTool in gatewayTools)
            {
                if (effectiveTools.Any(existingTool =>
                        string.Equals(existingTool.Name, gatewayTool.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                effectiveTools.Add(gatewayTool);
            }

            effectiveOptions.Tools = effectiveTools;
            return effectiveOptions;
        }

        private McpGatewaySearchResult? FindLatestSearchResult(IReadOnlyList<ChatMessage> messages)
        {
            var functionNamesByCallId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            McpGatewaySearchResult? latestSearchResult = null;

            foreach (var message in messages)
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent functionCall:
                            functionNamesByCallId[functionCall.CallId] = functionCall.Name;
                            break;
                        case FunctionResultContent functionResult
                            when functionNamesByCallId.TryGetValue(functionResult.CallId, out var functionName) &&
                                 string.Equals(functionName, _options.SearchToolName, StringComparison.OrdinalIgnoreCase):
                            latestSearchResult = ReadSearchResult(functionResult.Result);
                            break;
                    }
                }
            }

            return latestSearchResult;
        }

        private static McpGatewaySearchResult? ReadSearchResult(object? result)
        {
            if (result is McpGatewaySearchResult searchResult)
            {
                return searchResult;
            }

            var serialized = McpGatewayJsonSerializer.TrySerializeToElement(result);
            if (serialized is not JsonElement { ValueKind: JsonValueKind.Object } jsonElement)
            {
                return null;
            }

            try
            {
                return jsonElement.Deserialize<McpGatewaySearchResult>(McpGatewayJsonSerializer.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return null;
        }
    }
}
