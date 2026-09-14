using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public interface IIntegrationJobRepository
{
    Task AddAsync(IntegrationJob job, CancellationToken cancellationToken = default);
    Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
