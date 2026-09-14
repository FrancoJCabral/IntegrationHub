using IntegrationHub.Contracts;
using IntegrationHub.Worker.Execution;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Idempotency;

public sealed class IdempotentConnectorExecutor(
    IConnectorExecutor inner,
    IIdempotencyStore store,
    IOptions<IdempotencyOptions> options,
    TimeProvider clock,
    ILogger<IdempotentConnectorExecutor> logger) : IConnectorExecutor
{
    public async Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = Guid.NewGuid().ToString("N");
        // Start the cooperative execution budget BEFORE acquisition; reserve 20% of the lease for completion.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.LeaseSeconds * 0.8), clock);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        if (!await store.TryAcquireAsync(message.JobId, owner, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observability.JobsDuplicated.Add(1);
            System.Diagnostics.Activity.Current?.AddEvent(new("idempotency.duplicate"));
            logger.LogInformation("Duplicate detected for job {JobId}; skipping connector.", message.JobId);
            return;
        }

        var completed = false;
        try
        {
            execution.Token.ThrowIfCancellationRequested();
            await inner.ExecuteAsync(message, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
            if (!await store.CompleteAsync(message.JobId, owner, execution.Token))
                throw new InvalidOperationException("Idempotency lease ownership was lost.");
            completed = true;
            Observability.JobsProcessed.Add(1);
            logger.LogInformation("Idempotency completed for job {JobId}.", message.JobId);
        }
        finally
        {
            if (!completed)
            {
                try { await store.ReleaseAsync(message.JobId, owner, CancellationToken.None); }
                catch (Exception)
                {
                    // Preserve the original error/cancellation; the abandoned lease expires automatically.
                    logger.LogWarning("Could not release idempotency lease for job {JobId}; it will expire.", message.JobId);
                }
            }
        }
    }
}
