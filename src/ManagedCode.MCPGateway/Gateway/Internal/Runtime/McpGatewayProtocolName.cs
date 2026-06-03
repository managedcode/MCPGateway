using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolName
{
    public const int MaxNameLength = 64;
    private const int HashLength = 8;
    private const string DefaultToolName = "gateway_tool";
    private const string DefaultSourceName = "source";
    private const string DefaultResourceName = "resource";
    private const char Separator = '_';
    private static readonly char[] TrimCharacters = ['_', '-'];

    public static string CreateToolId(
        string sourceId,
        string toolName,
        ISet<string> reservedIds,
        bool requireSourcePrefix = false
    )
    {
        ArgumentNullException.ThrowIfNull(reservedIds);

        var normalizedToolName = Normalize(toolName, DefaultToolName);
        if (!requireSourcePrefix && reservedIds.Add(normalizedToolName))
        {
            return normalizedToolName;
        }

        var sourceQualifiedName = CreateSourceQualifiedName(
            sourceId,
            normalizedToolName,
            DefaultToolName
        );
        if (reservedIds.Add(sourceQualifiedName))
        {
            return sourceQualifiedName;
        }

        var hashedName = AppendHash(sourceQualifiedName, sourceId, toolName);
        if (reservedIds.Add(hashedName))
        {
            return hashedName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var uniqueName = AppendSuffix(
                hashedName,
                string.Concat(Separator, suffix.ToString(CultureInfo.InvariantCulture))
            );
            if (reservedIds.Add(uniqueName))
            {
                return uniqueName;
            }
        }
    }

    public static string CreateSourceQualifiedName(
        string sourceId,
        string name,
        string fallback = DefaultResourceName
    )
    {
        var normalizedSourceId = Normalize(sourceId, DefaultSourceName);
        var normalizedName = Normalize(name, fallback);
        if (HasSourcePrefix(normalizedName, normalizedSourceId))
        {
            return normalizedName;
        }

        return Truncate(string.Concat(normalizedSourceId, Separator, normalizedName));
    }

    public static string Normalize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Truncate(fallback);
        }

        var builder = new StringBuilder(value.Length);
        var lastWasReplacement = false;
        foreach (var character in value.Trim())
        {
            if (IsAllowedNameCharacter(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasReplacement = false;
                continue;
            }

            if (builder.Length == 0 || lastWasReplacement)
            {
                continue;
            }

            builder.Append(Separator);
            lastWasReplacement = true;
        }

        var normalized = builder.ToString().Trim(TrimCharacters);
        return normalized.Length == 0 ? Truncate(fallback) : Truncate(normalized);
    }

    private static bool HasSourcePrefix(string name, string sourceId) =>
        name.Length > sourceId.Length + 1
        && name.StartsWith(sourceId, StringComparison.OrdinalIgnoreCase)
        && name[sourceId.Length] is '_' or '-';

    private static bool IsAllowedNameCharacter(char character) =>
        character is >= 'a' and <= 'z'
        || character is >= 'A' and <= 'Z'
        || character is >= '0' and <= '9'
        || character is '_' or '-';

    private static string AppendHash(string value, string sourceId, string toolName)
    {
        var hashInput = string.Concat(sourceId, "\u001f", toolName);
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))
            .ToLowerInvariant()[..HashLength];

        return AppendSuffix(value, string.Concat(Separator, hash));
    }

    private static string AppendSuffix(string value, string suffix)
    {
        if (value.Length + suffix.Length <= MaxNameLength)
        {
            return string.Concat(value, suffix);
        }

        var prefixLength = Math.Max(1, MaxNameLength - suffix.Length);
        var prefix = value[..Math.Min(value.Length, prefixLength)].TrimEnd(TrimCharacters);
        if (prefix.Length == 0)
        {
            prefix = DefaultToolName[..Math.Min(DefaultToolName.Length, prefixLength)];
        }

        return string.Concat(prefix, suffix);
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxNameLength)
        {
            return value;
        }

        return value[..MaxNameLength].TrimEnd(TrimCharacters);
    }
}
