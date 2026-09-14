using IntegrationHub.Contracts;

namespace IntegrationHub.Worker.Execution;

// All three simulated systems currently do the same work; no artificial class hierarchy is needed.
public sealed class SimulatedExternalConnector : IExternalConnector
{
    public string Name { get; }

    public SimulatedExternalConnector(string name)
    {
        if (name is not ("Crm" or "Erp" or "Payments"))
            throw new PermanentConnectorException("Unsupported connector.");
        Name = name;
    }

    public async Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Connector != Name || string.IsNullOrWhiteSpace(message.Operation) ||
            string.IsNullOrWhiteSpace(message.ExternalId))
            throw new PermanentConnectorException("Invalid connector request.");
        await Task.Delay(10, cancellationToken);
    }
}
