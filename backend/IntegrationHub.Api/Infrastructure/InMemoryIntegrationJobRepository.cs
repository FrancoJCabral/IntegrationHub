using System.Collections.Concurrent;
using IntegrationHub.Application;
using IntegrationHub.Domain;

namespace IntegrationHub.Api.Infrastructure;

// Process-local storage only. No worker mutates the stored jobs in this stage.
public sealed class InMemoryIntegrationJobRepository : IIntegrationJobRepository
{
    private readonly ConcurrentDictionary<Guid, IntegrationJob> _jobs = new();

    public Task AddAsync(IntegrationJob job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(job);
        if (!_jobs.TryAdd(job.Id, job))
            throw new InvalidOperationException("A job with this ID already exists.");
        return Task.CompletedTask;
    }

    public Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }
}
