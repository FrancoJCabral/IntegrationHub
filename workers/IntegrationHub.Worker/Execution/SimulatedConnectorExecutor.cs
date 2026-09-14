using IntegrationHub.Contracts;

namespace IntegrationHub.Worker.Execution;

public sealed class SimulatedConnectorExecutor : IConnectorExecutor
{
    public async Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        if (message.Connector is not ("Crm" or "Erp" or "Payments"))
            throw new ArgumentException("Unsupported connector.", nameof(message));
        await Task.Delay(10, cancellationToken);
    }
}
