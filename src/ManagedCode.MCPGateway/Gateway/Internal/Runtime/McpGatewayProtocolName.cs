using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ManagedCode.MCPGateway;

internal static class McpGatewayProtocolName
{
    public const int MaxNameLength = 64;
    internal const string DefaultToolName = "gateway_tool";
    internal const string DefaultSourceName = "source";
    internal const string DefaultResourceName = "resource";
    private const int HashLength = 8;
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

        return Shorten(string.Concat(normalizedSourceId, Separator, normalizedName));
    }

    public static string Normalize(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Shorten(fallback);
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
        return normalized.Length == 0 ? Shorten(fallback) : Shorten(normalized);
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
        var hash = ComputeHash(sourceId, toolName);

        return AppendSuffix(value, string.Concat(Separator, hash));
    }

    internal static string AppendSuffix(string value, string suffix)
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

    private static string Shorten(string value)
    {
        if (value.Length <= MaxNameLength)
        {
            return value;
        }

        var suffix = string.Concat(Separator, ComputeHash(value));
        return AppendSuffix(value, suffix);
    }

    private static string ComputeHash(string value) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..HashLength];

    private static string ComputeHash(string firstValue, string secondValue)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashPart(hash, firstValue);
        AppendHashPart(hash, secondValue);

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..HashLength];
    }

    private static void AppendHashPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, bytes.Length);
        hash.AppendData(lengthPrefix);
        hash.AppendData(bytes);
    }
}
