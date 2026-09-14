using IntegrationHub.Contracts;

namespace IntegrationHub.Worker.Execution;

public interface IExternalConnector
{
    string Name { get; }
    Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken);
}
