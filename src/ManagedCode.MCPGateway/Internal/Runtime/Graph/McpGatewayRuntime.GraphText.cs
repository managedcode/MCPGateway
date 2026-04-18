using System.Text;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static string HumanizeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(identifier.Length + 8);
        var previousWasSeparator = false;
        var previousWasLowerOrDigit = false;

        foreach (var character in identifier.Trim())
        {
            if (
                char.IsWhiteSpace(character)
                || character is '_' or '-' or '.' or ',' or ';' or ':' or '/' or '\\'
            )
            {
                if (builder.Length > 0 && !previousWasSeparator)
                {
                    builder.Append(' ');
                }

                previousWasSeparator = true;
                previousWasLowerOrDigit = false;
                continue;
            }

            if (char.IsUpper(character) && previousWasLowerOrDigit && !previousWasSeparator)
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSeparator = false;
            previousWasLowerOrDigit = char.IsLower(character) || char.IsDigit(character);
        }

        return builder.ToString().Trim();
    }
}
