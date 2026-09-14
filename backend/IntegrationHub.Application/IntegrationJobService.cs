using IntegrationHub.Contracts;
using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public sealed class IntegrationJobService(
    IIntegrationJobRepository repository,
    IIntegrationJobPublisher publisher) : IIntegrationJobService
{
    public async Task<IntegrationJob> CreateAsync(ConnectorType connector, string operation, string externalId,
        CancellationToken cancellationToken = default)
    {
        var job = new IntegrationJob(connector, operation, externalId);
        await repository.AddAsync(job, cancellationToken);
        // Save and publish are not atomic. A publication failure leaves the saved job Pending.
        await publisher.PublishAsync(new IntegrationJobSubmittedMessage(
            job.Id, job.Connector.ToString(), job.Operation, job.ExternalId, DateTime.UtcNow), cancellationToken);
        return job;
    }

    public Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => repository.GetByIdAsync(id, cancellationToken);
}
