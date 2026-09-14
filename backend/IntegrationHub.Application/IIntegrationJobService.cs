using IntegrationHub.Domain;

namespace IntegrationHub.Application;

public interface IIntegrationJobService
{
    Task<IntegrationJob> CreateAsync(ConnectorType connector, string operation, string externalId,
        CancellationToken cancellationToken = default);
    Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
