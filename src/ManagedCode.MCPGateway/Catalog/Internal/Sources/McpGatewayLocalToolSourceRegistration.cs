#pragma warning disable MCPEXP001

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayLocalToolSourceRegistration(string sourceId, string? displayName)
    : McpGatewayToolSourceRegistration(sourceId, displayName)
{
    private readonly ConcurrentQueue<McpGatewayLoadedTool> _tools = new();
    private readonly ConcurrentDictionary<string, McpGatewayPrompt> _prompts = new(
        StringComparer.Ordinal
    );

    public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

    public void AddTool(AITool tool, McpGatewayToolSearchHints? searchHints = null) =>
        _tools.Enqueue(new McpGatewayLoadedTool(tool, searchHints));

    public void AddPrompt(McpGatewayPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (!_prompts.TryAdd(prompt.Name, prompt))
        {
            throw new InvalidOperationException(
                $"Prompt '{prompt.Name}' is already registered for source '{SourceId}'."
            );
        }
    }

    public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>(_tools.ToArray());

    public override ValueTask<IReadOnlyList<McpGatewayLoadedPrompt>> LoadPromptsAsync(
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedPrompt>>(
            _prompts
                .Values.OrderBy(static prompt => prompt.Name, StringComparer.Ordinal)
                .Select(static prompt => new McpGatewayLoadedPrompt(
                    new Prompt
                    {
                        Name = prompt.Name,
                        Title = prompt.DisplayName,
                        Description = prompt.Description,
                        Arguments = prompt
                            .Arguments.Select(static argument => new PromptArgument
                            {
                                Name = argument.Name,
                                Title = argument.DisplayName,
                                Description = argument.Description,
                                Required = argument.IsRequired,
                            })
                            .ToList(),
                    }
                ))
                .ToList()
        );

    public override async ValueTask<GetPromptResult?> GetPromptAsync(
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments,
        McpGatewayPromptInvocationContext? promptContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(promptName))
        {
            return null;
        }

        if (!_prompts.TryGetValue(promptName.Trim(), out var prompt))
        {
            return null;
        }

        var renderContext = new McpGatewayPromptRenderContext(
            SourceId,
            prompt.Name,
            arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            promptContext?.Services ?? EmptyServiceProvider.Instance,
            (request, token) =>
                promptContext?.RenderPromptAsync(request, token)
                ?? ValueTask.FromResult<GetPromptResult?>(null)
        );
        var result = await prompt.RenderAsync(renderContext, cancellationToken);
        ArgumentNullException.ThrowIfNull(result);

        result.Description = string.IsNullOrWhiteSpace(result.Description)
            ? prompt.Description
            : result.Description;
        return result;
    }

    public override async ValueTask<CompleteResult?> CompleteAsync(
        Reference reference,
        Argument argument,
        CompleteContext? context,
        IServiceProvider? serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (
            reference is not PromptReference promptReference
            || string.IsNullOrWhiteSpace(promptReference.Name)
        )
        {
            return null;
        }

        if (!_prompts.TryGetValue(promptReference.Name.Trim(), out var prompt))
        {
            return null;
        }

        if (prompt.CompleteAsync is null || string.IsNullOrWhiteSpace(argument.Name))
        {
            return null;
        }

        return await prompt.CompleteAsync(
            new McpGatewayPromptCompletionContext(
                SourceId,
                prompt.Name,
                argument.Name.Trim(),
                argument.Value ?? string.Empty,
                context,
                serviceProvider ?? EmptyServiceProvider.Instance
            ),
            cancellationToken
        );
    }
}
