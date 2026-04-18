using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestChatClient(TestChatClientOptions? options = null) : IChatClient
{
    private readonly TestChatClientOptions _options = options ?? new();

    public List<string> Calls { get; } = [];

    public List<TestChatClientInvocation> Invocations { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var query = messageList.LastOrDefault(static message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        Calls.Add(query);

        if (_options.ThrowOnInput?.Invoke(query) == true)
        {
            throw new InvalidOperationException("Query normalization failed for a test input.");
        }

        var invocation = new TestChatClientInvocation(messageList, options, Invocations.Count + 1);
        Invocations.Add(invocation);

        foreach (var scenario in _options.Scenarios)
        {
            if (scenario.When(invocation))
            {
                return Task.FromResult(scenario.Respond(invocation));
            }
        }

        if (_options.Scenarios.Count > 0)
        {
            throw new InvalidOperationException(
                $"No test chat scenario matched invocation #{invocation.InvocationIndex} for '{query}'.");
        }

        var rewrittenQuery = _options.RewriteQuery?.Invoke(query) ?? query;
        return Task.FromResult(TestChatClientScenario.Text(rewrittenQuery));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);

        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _options.Metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    public void Dispose()
    {
    }
}

internal sealed class TestChatClientOptions
{
    public ChatClientMetadata? Metadata { get; init; }

    public Func<string, string>? RewriteQuery { get; init; }

    public Func<string, bool>? ThrowOnInput { get; init; }

    public IReadOnlyList<TestChatClientScenario> Scenarios { get; init; } = [];
}

internal sealed class TestChatClientInvocation
{
    private readonly Dictionary<string, string> _functionNamesByCallId = new(StringComparer.OrdinalIgnoreCase);

    public TestChatClientInvocation(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        int invocationIndex)
    {
        InvocationIndex = invocationIndex;
        Messages = messages;
        Options = options;
        UserText = messages.LastOrDefault(static message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        ToolNames = options?.Tools?.Select(static tool => tool.Name).ToArray() ?? [];
        FunctionCalls = ExtractFunctionCalls(messages);
        FunctionResults = ExtractFunctionResults(messages);
    }

    public int InvocationIndex { get; }

    public IReadOnlyList<ChatMessage> Messages { get; }

    public ChatOptions? Options { get; }

    public string UserText { get; }

    public IReadOnlyList<string> ToolNames { get; }

    public IReadOnlyList<FunctionCallContent> FunctionCalls { get; }

    public IReadOnlyList<FunctionResultContent> FunctionResults { get; }

    public FunctionResultContent? FindLatestFunctionResult(string functionName)
    {
        ArgumentNullException.ThrowIfNull(functionName);

        for (var index = FunctionResults.Count - 1; index >= 0; index--)
        {
            var result = FunctionResults[index];
            if (_functionNamesByCallId.TryGetValue(result.CallId, out var matchedFunctionName)
                && string.Equals(matchedFunctionName, functionName, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        return null;
    }

    public int CountFunctionResults(string functionName)
    {
        ArgumentNullException.ThrowIfNull(functionName);

        var count = 0;
        for (var index = 0; index < FunctionResults.Count; index++)
        {
            var result = FunctionResults[index];
            if (_functionNamesByCallId.TryGetValue(result.CallId, out var matchedFunctionName) &&
                string.Equals(matchedFunctionName, functionName, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    public T? ReadLatestFunctionResult<T>(string functionName)
    {
        var functionResult = FindLatestFunctionResult(functionName);
        if (functionResult is null)
        {
            return default;
        }

        return TestChatClientScenario.ConvertResult<T>(functionResult.Result);
    }

    public JsonElement? ReadLatestFunctionResultElement(string functionName)
    {
        var functionResult = FindLatestFunctionResult(functionName);
        if (functionResult is null || functionResult.Result is null)
        {
            return null;
        }

        return functionResult.Result switch
        {
            JsonElement jsonElement => jsonElement,
            _ => JsonSerializer.SerializeToElement(functionResult.Result, TestChatClientScenario.JsonOptions)
        };
    }

    private static IReadOnlyList<FunctionCallContent> ExtractFunctionCalls(IReadOnlyList<ChatMessage> messages)
    {
        var functionCalls = new List<FunctionCallContent>();

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    functionCalls.Add(functionCall);
                }
            }
        }

        return functionCalls;
    }

    private IReadOnlyList<FunctionResultContent> ExtractFunctionResults(IReadOnlyList<ChatMessage> messages)
    {
        var functionResults = new List<FunctionResultContent>();

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent functionCall:
                        _functionNamesByCallId[functionCall.CallId] = functionCall.Name;
                        break;
                    case FunctionResultContent functionResult:
                        functionResults.Add(functionResult);
                        break;
                }
            }
        }

        return functionResults;
    }
}

internal sealed class TestChatClientScenario(
    string name,
    Func<TestChatClientInvocation, bool> when,
    Func<TestChatClientInvocation, ChatResponse> respond)
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public string Name { get; } = name;

    public Func<TestChatClientInvocation, bool> When { get; } = when;

    public Func<TestChatClientInvocation, ChatResponse> Respond { get; } = respond;

    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse FunctionCall(
        string callId,
        string functionName,
        IReadOnlyDictionary<string, object?>? arguments = null)
        => FunctionCalls(new TestChatFunctionCall(callId, functionName, arguments));

    public static ChatResponse FunctionCalls(params TestChatFunctionCall[] functionCalls)
    {
        ArgumentNullException.ThrowIfNull(functionCalls);

        var contents = new List<AIContent>(functionCalls.Length);
        foreach (var functionCall in functionCalls)
        {
            contents.Add(new FunctionCallContent(functionCall.CallId, functionCall.FunctionName, functionCall.ToArgumentsDictionary()));
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
    }

    public static T ConvertResult<T>(object? result)
    {
        if (result is null)
        {
            return default!;
        }

        if (result is T typedResult)
        {
            return typedResult;
        }

        return result switch
        {
            JsonElement jsonElement => jsonElement.Deserialize<T>(JsonOptions)!,
            _ => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, JsonOptions), JsonOptions)!
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record TestChatFunctionCall(
    string CallId,
    string FunctionName,
    IReadOnlyDictionary<string, object?>? Arguments = null)
{
    public Dictionary<string, object?> ToArgumentsDictionary()
    {
        var mappedArguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (Arguments is null)
        {
            return mappedArguments;
        }

        foreach (var (name, value) in Arguments)
        {
            if (value is not null)
            {
                mappedArguments[name] = value;
            }
        }

        return mappedArguments;
    }
}
