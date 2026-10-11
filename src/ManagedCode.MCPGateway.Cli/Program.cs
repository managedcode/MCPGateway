using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Cli;

internal static class Program
{
    private const string EndpointOption = "--endpoint";
    private const string ArgumentsFileOption = "--arguments-file";
    private const string OutputOption = "--output";
    private const string EndpointEnvironment = "MCPGATEWAY_ENDPOINT";
    private const string TokenEnvironment = "MCPGATEWAY_TOKEN";
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";
    private const string SearchTool = "gateway_tools_search";
    private const string InvokeTool = "gateway_tool_invoke";
    private const string SchemaTool = "gateway_tool_schema";
    private const string QueryArgument = "query";
    private const string ToolIdArgument = "toolId";
    private const string ArgumentsArgument = "arguments";
    private const string ToolsCommand = "tools";
    private const string SearchCommand = "search";
    private const string SchemaCommand = "schema";
    private const string CallCommand = "call";
    private const string HelpOption = "--help";
    private const string VersionOption = "--version";
    private const string Usage = """
        MCPGateway CLI
        mcpgateway tools search "query" --endpoint https://example.com/mcp
        mcpgateway tools schema tool_id --endpoint https://example.com/mcp
        mcpgateway tools call tool_id --arguments-file request.json --output result.json
        Endpoint: --endpoint or MCPGATEWAY_ENDPOINT. Token: MCPGATEWAY_TOKEN.
        Output files must not already exist. --help and --version need no endpoint.
        """;
    private const string InvalidCommandMessage = "Invalid command. Run mcpgateway --help.";
    private const string InvalidEndpointMessage = "Provide an absolute HTTP(S) MCP endpoint through --endpoint or MCPGATEWAY_ENDPOINT.";
    private const string InvocationFailedMessage = "MCP operation failed.";
    private const string CancelledMessage = "MCP operation cancelled.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args is [HelpOption])
        {
            await Console.Out.WriteLineAsync(Usage);
            return 0;
        }
        if (args is [VersionOption])
        {
            await Console.Out.WriteLineAsync(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(CancelledMessage);
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or ModelContextProtocol.McpException or HttpRequestException)
        {
            await Console.Error.WriteLineAsync(InvocationFailedMessage);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 3 || args[0] != ToolsCommand || args[1] is not (SearchCommand or SchemaCommand or CallCommand))
            throw new ArgumentException(InvalidCommandMessage);
        var options = ParseOptions(args);
        var endpointText = options.GetValueOrDefault(EndpointOption) ?? Environment.GetEnvironmentVariable(EndpointEnvironment);
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException(InvalidEndpointMessage);
        var token = Environment.GetEnvironmentVariable(TokenEnvironment);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = string.IsNullOrWhiteSpace(token) ? null : new Dictionary<string, string> { [AuthorizationHeader] = BearerPrefix + token },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var output = args[1] switch
        {
            SearchCommand => await client.CallToolAsync(SearchTool, new Dictionary<string, object?> { [QueryArgument] = args[2] }, cancellationToken: cancellationToken),
            SchemaCommand => await GetSchemaAsync(client, args[2], cancellationToken),
            _ => await CallAsync(client, args[2], options.GetValueOrDefault(ArgumentsFileOption), cancellationToken),
        };
        var json = JsonSerializer.Serialize(output, JsonOptions);
        if (options.TryGetValue(OutputOption, out var outputPath))
        {
            await using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json.AsMemory(), cancellationToken);
        }
        else await Console.Out.WriteLineAsync(json);
        return output is CallToolResult { IsError: true } ? 2 : 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 3; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not (EndpointOption or ArgumentsFileOption or OutputOption) || !options.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException(InvalidCommandMessage);
        }
        return options;
    }

    private static async Task<object> GetSchemaAsync(McpClient client, string toolId, CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var schemaTool = tools.FirstOrDefault(tool => tool.Name == SchemaTool);
        if (schemaTool is not null)
            return await schemaTool.CallAsync(new Dictionary<string, object?> { [ToolIdArgument] = toolId }, cancellationToken: cancellationToken);
        return tools.FirstOrDefault(tool => string.Equals(tool.Name, toolId, StringComparison.OrdinalIgnoreCase))?.ProtocolTool
            ?? throw new ArgumentException(InvalidCommandMessage);
    }

    private static async Task<CallToolResult> CallAsync(McpClient client, string toolId, string? argumentsPath, CancellationToken cancellationToken)
    {
        Dictionary<string, object?>? arguments = null;
        if (argumentsPath is not null)
        {
            var json = await File.ReadAllTextAsync(argumentsPath, cancellationToken);
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions) ?? throw new JsonException(InvalidCommandMessage);
        }
        return await client.CallToolAsync(InvokeTool, new Dictionary<string, object?>
        {
            [ToolIdArgument] = toolId, [ArgumentsArgument] = arguments,
        }, cancellationToken: cancellationToken);
    }
}
