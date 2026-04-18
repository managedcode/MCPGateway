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
        CancellationToken cancellationToken)
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

        if (_searchRuntimeCache.TryGetNormalizedQuery(_searchQueryNormalization, trimmedQuery, out var cachedNormalizedQuery))
        {
            if (cachedNormalizedQuery is not null)
            {
                diagnostics.Add(new McpGatewayDiagnostic(QueryNormalizedDiagnosticCode, QueryNormalizedMessage));
            }

            return cachedNormalizedQuery;
        }

        try
        {
            await using var chatClientLease = ResolveSearchQueryChatClient();
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
                    MaxOutputTokens = SearchQueryNormalizationMaxOutputTokens
                },
                cancellationToken);

            var normalizedQuery = NormalizeChatResponseText(response.Text);
            if (string.IsNullOrWhiteSpace(normalizedQuery) ||
                string.Equals(normalizedQuery, trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                _searchRuntimeCache.SetNormalizedQuery(_searchQueryNormalization, trimmedQuery, null);
                return null;
            }

            _searchRuntimeCache.SetNormalizedQuery(_searchQueryNormalization, trimmedQuery, normalizedQuery);
            diagnostics.Add(new McpGatewayDiagnostic(QueryNormalizedDiagnosticCode, QueryNormalizedMessage));
            return normalizedQuery;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(new McpGatewayDiagnostic(
                QueryNormalizationFailedDiagnosticCode,
                string.Format(
                    CultureInfo.InvariantCulture,
                    QueryNormalizationFailedMessageFormat,
                    ex.GetBaseException().Message)));
            _logger.LogWarning(ex, GatewayQueryNormalizationFailedLogMessage);
            return null;
        }
    }

    private ChatClientLease ResolveSearchQueryChatClient()
    {
        if (_serviceProvider.GetService(typeof(IServiceScopeFactory)) is not IServiceScopeFactory scopeFactory)
        {
            return new ChatClientLease(ResolveSearchQueryChatClient(_serviceProvider));
        }

        var scope = scopeFactory.CreateAsyncScope();
        var chatClient = ResolveSearchQueryChatClient(scope.ServiceProvider);
        return new ChatClientLease(chatClient, scope);
    }

    private static IChatClient? ResolveSearchQueryChatClient(IServiceProvider serviceProvider)
        => serviceProvider.GetKeyedService<IChatClient>(McpGatewayServiceKeys.SearchQueryChatClient);

    private static string? NormalizeChatResponseText(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var normalized = responseText.Trim();
        normalized = normalized.Trim('`', '"', '\'', ' ');
        normalized = normalized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Trim().Trim('`', '"', '\'');
    }
}
