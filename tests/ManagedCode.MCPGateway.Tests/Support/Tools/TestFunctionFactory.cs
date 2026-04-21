using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

internal static class TestFunctionFactory
{
    public static AIFunction CreateFunction(Delegate callback, string name, string description) =>
        CreateFunction(callback, name, description, additionalProperties: null);

    public static AIFunction CreateFunction(
        Delegate callback,
        string name,
        string description,
        IReadOnlyDictionary<string, object?>? additionalProperties
    ) =>
        AIFunctionFactory.Create(
            callback,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description,
                AdditionalProperties = additionalProperties is null
                    ? null
                    : new Dictionary<string, object?>(additionalProperties, StringComparer.Ordinal),
            }
        );
}
