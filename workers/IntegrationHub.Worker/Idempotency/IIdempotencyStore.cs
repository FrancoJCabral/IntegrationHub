namespace IntegrationHub.Worker.Idempotency;

public interface IIdempotencyStore
{
    Task<bool> TryAcquireAsync(Guid jobId, string owner, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string owner, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid jobId, string owner, CancellationToken cancellationToken);
}
