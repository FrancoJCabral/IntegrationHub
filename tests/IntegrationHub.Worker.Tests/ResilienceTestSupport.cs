using IntegrationHub.Contracts;
using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Tests;

internal sealed class TestConnector(string name, Func<int, CancellationToken, Task> behavior) : IExternalConnector
{
    private int _calls;
    public string Name => name;
    public int Calls => Volatile.Read(ref _calls);
    public Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken) =>
        behavior(Interlocked.Increment(ref _calls), cancellationToken);
}

internal static class ResilienceTestSupport
{
    public static IntegrationJobSubmittedMessage Message(string connector = "Crm") =>
        new(Guid.NewGuid(), connector, "Sync", "not-for-logs", DateTime.UtcNow);

    public static ConnectorResilienceOptions Settings(int retries = 3) => new()
    {
        TimeoutSeconds = 1,
        MaxRetryAttempts = retries,
        BaseDelayMilliseconds = 0,
        CircuitBreaker = new() { FailureRatio = 1, MinimumThroughput = 2,
            SamplingDurationSeconds = 30, BreakDurationSeconds = 15 }
    };

    public static ResilientConnectorExecutor Executor(IEnumerable<IExternalConnector> connectors,
        ConnectorResilienceOptions? settings = null, TimeProvider? clock = null,
        ILogger<ResilientConnectorExecutor>? logger = null) =>
        new(connectors, Options.Create(settings ?? Settings()), clock ?? TimeProvider.System,
            logger ?? NullLogger<ResilientConnectorExecutor>.Instance);
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
}
