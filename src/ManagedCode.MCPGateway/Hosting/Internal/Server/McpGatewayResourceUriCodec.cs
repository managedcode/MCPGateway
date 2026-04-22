using System.Text;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayResourceUriCodec
{
    private const string GatewaySchemePrefix = "mcpgw-";
    private const string OpaqueSchemeName = "opaque";

    public static string ToGatewayUri(string sourceId, string resourceUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);

        var encodedSourceId = Convert.ToHexString(Encoding.UTF8.GetBytes(sourceId.Trim()));
        var trimmedUri = resourceUri.Trim();

        if (TrySplitScheme(trimmedUri, out var scheme, out var remainder))
        {
            return $"{GatewaySchemePrefix}{encodedSourceId}+{scheme}{remainder}";
        }

        return $"{GatewaySchemePrefix}{encodedSourceId}+{OpaqueSchemeName}:{Uri.EscapeDataString(trimmedUri)}";
    }

    public static bool TryDecodeGatewayUri(
        string gatewayUri,
        out string sourceId,
        out string resourceUri
    )
    {
        sourceId = string.Empty;
        resourceUri = string.Empty;

        if (!TrySplitScheme(gatewayUri, out var scheme, out var remainder))
        {
            return false;
        }

        if (!scheme.StartsWith(GatewaySchemePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = scheme[GatewaySchemePrefix.Length..];
        var separatorIndex = payload.IndexOf('+');
        if (separatorIndex <= 0 || separatorIndex == payload.Length - 1)
        {
            return false;
        }

        var encodedSourceId = payload[..separatorIndex];
        var originalScheme = payload[(separatorIndex + 1)..];

        try
        {
            sourceId = Encoding.UTF8.GetString(Convert.FromHexString(encodedSourceId));
        }
        catch (FormatException)
        {
            sourceId = string.Empty;
            return false;
        }

        resourceUri = string.Equals(originalScheme, OpaqueSchemeName, StringComparison.Ordinal)
            ? Uri.UnescapeDataString(remainder.TrimStart(':'))
            : $"{originalScheme}{remainder}";

        return true;
    }

    private static bool TrySplitScheme(
        string value,
        out string scheme,
        out string remainder
    )
    {
        scheme = string.Empty;
        remainder = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0 || colonIndex == value.Length - 1)
        {
            return false;
        }

        var candidateScheme = value[..colonIndex];
        if (!IsValidScheme(candidateScheme))
        {
            return false;
        }

        scheme = candidateScheme;
        remainder = value[colonIndex..];
        return true;
    }

    private static bool IsValidScheme(string value)
    {
        if (string.IsNullOrEmpty(value) || !char.IsLetter(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var current = value[index];
            if (!(char.IsLetterOrDigit(current) || current is '+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
