using IntegrationHub.Contracts;

namespace IntegrationHub.Application;

public interface IIntegrationJobPublisher
{
    Task PublishAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken = default);
}
