#:package ModelContextProtocol@2.2.0
#:property PublishAot=false
#:property ManagePackageVersionsCentrally=false
#:property ImportDirectoryBuildProps=false
#:property ImportDirectoryBuildTargets=false
#:property GenerateDocumentationFile=false

using System.Text.Json;
using ModelContextProtocol.Client;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: invoke.cs <capture-root> <response-directory>");
    return 2;
}

var captureRoot = Path.GetFullPath(args[0]);
var outputRoot = Path.GetFullPath(args[1]);
Directory.CreateDirectory(captureRoot);
Directory.CreateDirectory(outputRoot);
var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
environment["MCP_ARTIFACT_ROOT"] = captureRoot;
foreach (var name in new[] { "DOTNET_ROOT", "TMPDIR" })
{
    if (Environment.GetEnvironmentVariable(name) is { } value)
    {
        environment[name] = value;
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Command = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_SERVER") ?? "dotnet-diagnostics-mcp",
    Arguments = ["--stdio"],
    EnvironmentVariables = environment,
    InheritEnvironmentVariables = false,
    StandardErrorLines = line => Console.Error.WriteLine(line),
}), cancellationToken: cancellation.Token);

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var sequence = 0;
Console.WriteLine("READY: send JSON requests; EOF closes the diagnostic session.");
try
{
    while (!cancellation.IsCancellationRequested && await Console.In.ReadLineAsync(cancellation.Token) is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }
        using var request = JsonDocument.Parse(line);
        var name = request.RootElement.GetProperty("tool").GetString()
            ?? throw new ArgumentException("tool is required");
        var timeoutSeconds = request.RootElement.TryGetProperty("timeoutSeconds", out var timeout)
            ? timeout.GetInt32() : 120;
        if (timeoutSeconds < 1)
        {
            throw new ArgumentException("timeoutSeconds must be positive.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var requestSequence = ++sequence;
        var requestPath = Path.Combine(outputRoot, $"{requestSequence:D3}-request.json");
        await using (var requestOutput = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(requestOutput, new { request = request.RootElement }, jsonOptions, cancellation.Token);
        }
        object response;
        try
        {
            if (name == "tools/list")
            {
                var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                response = new { tools = tools.Select(tool => tool.ProtocolTool).ToArray() };
            }
            else
            {
                var arguments = request.RootElement.TryGetProperty("arguments", out var argumentsJson)
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson.GetRawText())
                    : null;
                response = await client.CallToolAsync(name, arguments, cancellationToken: deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            response = new { clientFailure = "deadline_expired", nativeResponseUnavailable = true };
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            response = new { clientFailure = "transport_or_client_error", exceptionType = error.GetType().FullName, nativeResponseUnavailable = true };
        }
        var outputName = $"{requestSequence:D3}-response.json";
        var path = Path.Combine(outputRoot, outputName);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(output, new { request = request.RootElement, response }, jsonOptions, cancellation.Token);
        Console.WriteLine($"Saved {name}: {path}");
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Diagnostic session cancelled.");
    return 130;
}
return 0;
