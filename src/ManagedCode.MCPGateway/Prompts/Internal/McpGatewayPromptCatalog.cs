using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptCatalog(
    IMcpGatewayCatalogSource catalogSource,
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory
) : IMcpGatewayPromptCatalog
{
    public async Task<IReadOnlyList<McpGatewayPromptDescriptor>> ListPromptsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = catalogSource.CreateSnapshot();
        var descriptors = new List<McpGatewayPromptDescriptor>();

        foreach (var registration in snapshot.Registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var prompts = await registration.LoadPromptsAsync(loggerFactory, cancellationToken);
            descriptors.AddRange(prompts.Select(prompt => BuildDescriptor(registration, prompt)));
        }

        return descriptors
            .OrderBy(static descriptor => descriptor.SourceId, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.PromptName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<McpGatewayPromptResult?> GetPromptAsync(
        McpGatewayPromptRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PromptName))
        {
            throw new ArgumentException("A prompt name is required.", nameof(request));
        }

        var sourceId = request.SourceId.Trim();
        var promptName = request.PromptName.Trim();
        var registration = FindRegistration(sourceId);
        if (registration is null)
        {
            return null;
        }

        var promptResult = await RenderPromptProtocolAsync(
            new McpGatewayPromptRequest(sourceId, promptName, request.Arguments),
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken
        );

        return ConvertPromptResult(promptResult, sourceId, promptName, registration.Kind);
    }

    internal Task<GetPromptResult?> RenderPromptProtocolAsync(
        McpGatewayPromptRequest request,
        CancellationToken cancellationToken = default
    ) =>
        RenderPromptProtocolAsync(
            request,
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken
        );

    private async Task<GetPromptResult?> RenderPromptProtocolAsync(
        McpGatewayPromptRequest request,
        HashSet<string> activePromptIds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            throw new ArgumentException("A source id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PromptName))
        {
            throw new ArgumentException("A prompt name is required.", nameof(request));
        }

        var sourceId = request.SourceId.Trim();
        var promptName = request.PromptName.Trim();
        var promptId = $"{sourceId}:{promptName}";
        if (!activePromptIds.Add(promptId))
        {
            throw new InvalidOperationException(
                $"Prompt '{promptId}' recursively references itself."
            );
        }

        var snapshot = catalogSource.CreateSnapshot();
        var registration = snapshot.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
        );
        if (registration is null)
        {
            activePromptIds.Remove(promptId);
            return null;
        }

        try
        {
            return await registration.GetPromptAsync(
                promptName,
                request.Arguments,
                new McpGatewayPromptInvocationContext(
                    serviceProvider,
                    (nestedRequest, token) =>
                        new ValueTask<GetPromptResult?>(
                            RenderPromptProtocolAsync(nestedRequest, activePromptIds, token)
                        )
                ),
                loggerFactory,
                cancellationToken
            );
        }
        finally
        {
            activePromptIds.Remove(promptId);
        }
    }

    private static McpGatewayPromptDescriptor BuildDescriptor(
        McpGatewayToolSourceRegistration registration,
        McpGatewayLoadedPrompt prompt
    )
    {
        var arguments = prompt
            .Arguments.Where(static argument => !string.IsNullOrWhiteSpace(argument.Name))
            .Select(static argument => new McpGatewayPromptArgumentDescriptor(
                Name: argument.Name.Trim(),
                DisplayName: string.IsNullOrWhiteSpace(argument.Title)
                    ? null
                    : argument.Title.Trim(),
                Description: argument.Description ?? string.Empty,
                IsRequired: argument.Required == true
            ))
            .ToList();

        return new McpGatewayPromptDescriptor(
            PromptId: $"{registration.SourceId}:{prompt.Name}",
            SourceId: registration.SourceId,
            SourceKind: McpGatewaySourceKindMapper.Map(registration.Kind),
            PromptName: prompt.Name,
            DisplayName: string.IsNullOrWhiteSpace(prompt.Title) ? null : prompt.Title.Trim(),
            Description: prompt.Description ?? string.Empty,
            Arguments: arguments
        )
        {
            RequiredArguments = arguments
                .Where(static argument => argument.IsRequired)
                .Select(static argument => argument.Name)
                .ToList(),
        };
    }

    private McpGatewayToolSourceRegistration? FindRegistration(string sourceId)
    {
        var snapshot = catalogSource.CreateSnapshot();
        return snapshot.Registrations.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal)
        );
    }

    private static McpGatewayPromptResult? ConvertPromptResult(
        GetPromptResult? promptResult,
        string sourceId,
        string promptName,
        McpGatewaySourceRegistrationKind sourceKind
    )
    {
        if (promptResult is null)
        {
            return null;
        }

        return new McpGatewayPromptResult(
            PromptId: $"{sourceId}:{promptName}",
            SourceId: sourceId,
            SourceKind: McpGatewaySourceKindMapper.Map(sourceKind),
            PromptName: promptName,
            Description: promptResult.Description ?? string.Empty,
            Messages: promptResult
                .Messages.Select(static message => new McpGatewayPromptMessage(
                    Role: message.Role.ToString(),
                    Content: McpGatewayJsonSerializer.TrySerializeToNode(message.Content),
                    Text: message.Content is TextContentBlock textContent ? textContent.Text : null
                ))
                .ToList()
        );
    }
}
