using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<string?> NormalizeSearchQueryAsync(
        string? query,
        ICollection<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (_searchQueryNormalization == McpGatewaySearchQueryNormalization.Disabled)
        {
            return null;
        }

        var trimmedQuery = NormalizeSearchComponent(query);
        if (trimmedQuery is null)
        {
            return null;
        }

        await using var chatClientLease = ResolveSearchQueryChatClient();
        var chatClientFingerprint = chatClientLease.Fingerprint;
        var cachedNormalizedQuery = await _searchRuntimeCache.TryGetNormalizedQueryAsync(
            _searchQueryNormalization,
            trimmedQuery,
            chatClientFingerprint,
            cancellationToken
        );
        if (cachedNormalizedQuery.found)
        {
            if (cachedNormalizedQuery.normalizedQuery is not null)
            {
                diagnostics.Add(
                    new McpGatewayDiagnostic(QueryNormalizedDiagnosticCode, QueryNormalizedMessage)
                );
            }

            return cachedNormalizedQuery.normalizedQuery;
        }

        try
        {
            if (chatClientLease.Client is not IChatClient chatClient)
            {
                return null;
            }

            var response = await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, trimmedQuery)],
                new ChatOptions
                {
                    Instructions = SearchQueryNormalizationInstructions,
                    Temperature = 0f,
                    MaxOutputTokens = SearchQueryNormalizationMaxOutputTokens,
                },
                cancellationToken
            );

            var normalizedQuery = NormalizeChatResponseText(response.Text);
            if (
                string.IsNullOrWhiteSpace(normalizedQuery)
                || string.Equals(normalizedQuery, trimmedQuery, StringComparison.OrdinalIgnoreCase)
            )
            {
                await _searchRuntimeCache.SetNormalizedQueryAsync(
                    _searchQueryNormalization,
                    trimmedQuery,
                    chatClientFingerprint,
                    null,
                    cancellationToken
                );
                return null;
            }

            await _searchRuntimeCache.SetNormalizedQueryAsync(
                _searchQueryNormalization,
                trimmedQuery,
                chatClientFingerprint,
                normalizedQuery,
                cancellationToken
            );
            diagnostics.Add(
                new McpGatewayDiagnostic(QueryNormalizedDiagnosticCode, QueryNormalizedMessage)
            );
            return normalizedQuery;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    QueryNormalizationFailedDiagnosticCode,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        QueryNormalizationFailedMessageFormat,
                        ex.GetBaseException().Message
                    )
                )
            );
            _logger.LogWarning(ex, GatewayQueryNormalizationFailedLogMessage);
            return null;
        }
    }

    private ChatClientLease ResolveSearchQueryChatClient()
    {
        if (
            _serviceProvider.GetService(typeof(IServiceScopeFactory))
            is not IServiceScopeFactory scopeFactory
        )
        {
            var rootChatClient = ResolveSearchQueryChatClient(_serviceProvider);
            return new ChatClientLease(
                rootChatClient,
                GetOrCreateSearchQueryChatClientFingerprint(rootChatClient)
            );
        }

        var scope = scopeFactory.CreateAsyncScope();
        var scopedChatClient = ResolveSearchQueryChatClient(scope.ServiceProvider);
        return new ChatClientLease(
            scopedChatClient,
            GetOrCreateSearchQueryChatClientFingerprint(scopedChatClient),
            scope
        );
    }

    private static IChatClient? ResolveSearchQueryChatClient(IServiceProvider serviceProvider) =>
        serviceProvider.GetKeyedService<IChatClient>(McpGatewayServiceKeys.SearchQueryChatClient);

    private static string? NormalizeChatResponseText(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var normalized = responseText.Trim();
        normalized = normalized.Trim('`', '"', '\'', ' ');
        normalized = normalized
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Trim().Trim('`', '"', '\'');
    }
}
