using IntegrationHub.Contracts;

namespace IntegrationHub.Worker.Execution;

public interface IConnectorExecutor
{
    Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken);
}
