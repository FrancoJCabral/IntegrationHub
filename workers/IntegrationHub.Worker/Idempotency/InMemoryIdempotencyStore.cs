using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Idempotency;

public sealed class InMemoryIdempotencyStore(IOptions<IdempotencyOptions> options, TimeProvider clock) : IIdempotencyStore
{
    private sealed record Entry(string? Owner, DateTimeOffset ExpiresAt);
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly object _gate = new();

    public Task<bool> TryAcquireAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            // Local demo store: reclaim expired records rather than keeping every historical job.
            foreach (var id in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                _entries.Remove(id);
            if (_entries.ContainsKey(jobId)) return Task.FromResult(false);
            _entries.Add(jobId, new(owner, now.AddSeconds(options.Value.LeaseSeconds)));
            return Task.FromResult(true);
        }
    }

    public Task<bool> CompleteAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(jobId, out var entry) || entry.Owner != owner || entry.ExpiresAt <= clock.GetUtcNow())
                return Task.FromResult(false);
            _entries[jobId] = new(null, clock.GetUtcNow().AddHours(options.Value.RetentionHours));
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_entries.TryGetValue(jobId, out var entry) && entry.Owner == owner)
                _entries.Remove(jobId);
        return Task.CompletedTask;
    }
}
