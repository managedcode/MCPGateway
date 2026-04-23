namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayServerBinding : IAsyncDisposable
{
    IMcpGateway Gateway { get; }

    IMcpGatewayPromptCatalog PromptCatalog { get; }

    IMcpGatewayResourceCatalog ResourceCatalog { get; }

    IMcpGatewayRegistry Registry { get; }

    IDisposable SubscribeToPromptListChanges(Action onChanged);

    ValueTask<IReadOnlyList<IMcpGatewayServerSource>> ListSourcesAsync(
        CancellationToken cancellationToken = default
    );
}
