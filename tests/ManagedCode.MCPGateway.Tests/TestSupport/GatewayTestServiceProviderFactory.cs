using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

internal static class GatewayTestServiceProviderFactory
{
    public static ServiceProvider Create(
        Action<McpGatewayOptions> configure,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.SetMinimumLevel(LogLevel.Debug));

        if (embeddingGenerator is not null)
        {
            services.AddSingleton(embeddingGenerator);
        }

        services.AddManagedCodeMcpGateway(configure);
        return services.BuildServiceProvider();
    }
}
