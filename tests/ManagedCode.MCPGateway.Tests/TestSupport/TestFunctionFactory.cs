using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Tests;

internal static class TestFunctionFactory
{
    public static AIFunction CreateFunction(Delegate callback, string name, string description)
        => AIFunctionFactory.Create(
            callback,
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });
}
