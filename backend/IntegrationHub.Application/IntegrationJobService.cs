using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public sealed class IntegrationJobService(IIntegrationJobRepository repository) : IIntegrationJobService
{
    public async Task<IntegrationJob> CreateAsync(ConnectorType connector, string operation, string externalId,
        CancellationToken cancellationToken = default)
    {
        var job = new IntegrationJob(connector, operation, externalId);
        await repository.AddAsync(job, cancellationToken);
        return job;
    }

    public Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => repository.GetByIdAsync(id, cancellationToken);
}
