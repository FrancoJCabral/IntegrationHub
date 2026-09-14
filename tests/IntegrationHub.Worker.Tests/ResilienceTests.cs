using System.Threading.Channels;
using IntegrationHub.Worker.Execution;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Polly.Timeout;
using static IntegrationHub.Worker.Tests.ResilienceTestSupport;

namespace IntegrationHub.Worker.Tests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task Success_executes_once()
    {
        var connector = new TestConnector("Crm", (_, _) => Task.CompletedTask);
        await Executor([connector]).ExecuteAsync(Message(), CancellationToken.None);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task Two_transient_failures_then_success_executes_three_times()
    {
        var connector = new TestConnector("Crm", (call, _) => call <= 2
            ? Task.FromException(new TransientConnectorException("private diagnostic")) : Task.CompletedTask);
        var logger = new CapturingLogger<ResilientConnectorExecutor>();
        await Executor([connector], logger: logger).ExecuteAsync(Message(), CancellationToken.None);
        Assert.Equal(3, connector.Calls);
        Assert.Equal(2, logger.Entries.Count(x => x.StartsWith("Retry ")));
        Assert.DoesNotContain(logger.Entries, x => x.Contains("private diagnostic") || x.Contains("not-for-logs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Exhausted_transient_failure_has_exact_attempt_count(int retries)
    {
        var connector = new TestConnector("Crm", (_, _) => Task.FromException(new TransientConnectorException("temporary")));
        await Assert.ThrowsAsync<TransientConnectorException>(() =>
            Executor([connector], Settings(retries)).ExecuteAsync(Message(), CancellationToken.None));
        Assert.Equal(1 + retries, connector.Calls);
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("validation")]
    [InlineData("unexpected")]
    public async Task Non_transient_failures_are_not_retried(string kind)
    {
        Exception failure = kind switch
        {
            "permanent" => new PermanentConnectorException("invalid operation"),
            "validation" => new ArgumentException("invalid data"),
            _ => new InvalidOperationException("unexpected")
        };
        var connector = new TestConnector("Crm", (_, _) => Task.FromException(failure));
        var actual = await Record.ExceptionAsync(() => Executor([connector]).ExecuteAsync(Message(), CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task Unsupported_connector_is_permanent_without_executing_another_connector()
    {
        var connector = new TestConnector("Crm", (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<PermanentConnectorException>(() =>
            Executor([connector]).ExecuteAsync(Message("Unknown"), CancellationToken.None));
        Assert.Equal(0, connector.Calls);
    }

    [Fact]
    public async Task Timeout_is_per_attempt_and_retried_then_propagated_using_fake_time()
    {
        var clock = new FakeTimeProvider();
        var starts = Channel.CreateUnbounded<int>();
        var connector = new TestConnector("Crm", async (call, token) =>
        {
            starts.Writer.TryWrite(call);
            await Task.Delay(Timeout.Infinite, token);
        });
        var operation = Executor([connector], Settings(2), clock).ExecuteAsync(Message(), CancellationToken.None);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(attempt, await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(operation.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, connector.Calls);
    }

    [Fact]
    public async Task Cancellation_during_execution_propagates_without_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new TestConnector("Crm", async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var operation = Executor([connector]).ExecuteAsync(Message(), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, connector.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Executor([connector]).ExecuteAsync(Message(), cancellation.Token));
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task Breaker_counts_logical_operations_and_isolates_connectors_then_recovers()
    {
        var clock = new FakeTimeProvider();
        var recovered = false;
        var crm = new TestConnector("Crm", (_, _) => recovered
            ? Task.CompletedTask : Task.FromException(new TransientConnectorException("temporary")));
        var erp = new TestConnector("Erp", (_, _) => Task.CompletedTask);
        var payments = new TestConnector("Payments", (_, _) => Task.CompletedTask);
        var logger = new CapturingLogger<ResilientConnectorExecutor>();
        var executor = Executor([crm, erp, payments], Settings(3), clock, logger);

        // Four failed attempts count as ONE operation, so the next operation must still run.
        await Assert.ThrowsAsync<TransientConnectorException>(() => executor.ExecuteAsync(Message(), CancellationToken.None));
        Assert.Equal(4, crm.Calls);
        await Assert.ThrowsAsync<TransientConnectorException>(() => executor.ExecuteAsync(Message(), CancellationToken.None));
        Assert.Equal(8, crm.Calls);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => executor.ExecuteAsync(Message(), CancellationToken.None));
        Assert.Equal(8, crm.Calls);
        await executor.ExecuteAsync(Message("Erp"), CancellationToken.None);
        await executor.ExecuteAsync(Message("Payments"), CancellationToken.None);
        Assert.Equal(1, erp.Calls);
        Assert.Equal(1, payments.Calls);

        recovered = true;
        clock.Advance(TimeSpan.FromSeconds(15));
        await executor.ExecuteAsync(Message(), CancellationToken.None);
        await executor.ExecuteAsync(Message(), CancellationToken.None);
        Assert.Equal(10, crm.Calls);
        Assert.Contains(logger.Entries, x => x.Contains("Crm circuit Open"));
        Assert.Contains(logger.Entries, x => x.Contains("Crm circuit HalfOpen"));
        Assert.Contains(logger.Entries, x => x.Contains("Crm circuit Closed"));
        Assert.DoesNotContain(logger.Entries, x => x.Contains("Erp circuit Open") || x.Contains("Payments circuit Open"));
    }

    [Fact]
    public async Task Permanent_failures_do_not_open_availability_circuit()
    {
        var connector = new TestConnector("Crm", (_, _) => Task.FromException(new PermanentConnectorException("invalid")));
        var executor = Executor([connector]);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<PermanentConnectorException>(() => executor.ExecuteAsync(Message(), CancellationToken.None));
        Assert.Equal(3, connector.Calls);
    }
}
