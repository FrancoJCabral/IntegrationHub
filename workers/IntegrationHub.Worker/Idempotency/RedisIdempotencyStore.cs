using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IntegrationHub.Worker.Idempotency;

public sealed class RedisIdempotencyStore(Func<IDatabase> getDatabase, IOptions<IdempotencyOptions> options) : IIdempotencyStore
{
    private const string AcquireScript = """
        if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'EX', ARGV[2]) then return 1 end
        return 0
        """;
    private const string CompleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('SET', KEYS[1], 'completed', 'EX', ARGV[2])
            return 1
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    public async Task<bool> TryAcquireAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Await the command even if shutdown arrives: do not abandon an acquisition with an unknown result.
        return (long)await getDatabase().ScriptEvaluateAsync(AcquireScript, [Key(jobId)],
            [Owner(owner), options.Value.LeaseSeconds]) == 1;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (long)await getDatabase().ScriptEvaluateAsync(CompleteScript, [Key(jobId)],
            [Owner(owner), options.Value.RetentionHours * 3600L]) == 1;
    }

    public async Task ReleaseAsync(Guid jobId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await getDatabase().ScriptEvaluateAsync(ReleaseScript, [Key(jobId)], [Owner(owner)]);
    }

    private static RedisKey Key(Guid jobId) => $"integrationhub:processed:{jobId:D}";
    private static RedisValue Owner(string owner) => $"processing:{owner}";
}
