using System.Text.Json;
using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RabbitMQ.Client;
using static IntegrationHub.Worker.Tests.ResilienceTestSupport;

namespace IntegrationHub.Worker.Tests;

public sealed class ResilientConsumerTests
{
    [Theory]
    [InlineData("recover", 3, true)]
    [InlineData("exhaust", 4, false)]
    [InlineData("permanent", 1, false)]
    [InlineData("open", 8, false)]
    public async Task Consumer_acknowledges_final_resilience_outcome(string scenario, int expectedCalls, bool ack)
    {
        var connector = new TestConnector("Crm", (call, _) => scenario switch
        {
            "recover" when call > 2 => Task.CompletedTask,
            "permanent" => Task.FromException(new PermanentConnectorException("invalid")),
            _ => Task.FromException(new TransientConnectorException("temporary"))
        });
        var executor = Executor([connector]);
        if (scenario == "open")
            for (var i = 0; i < 2; i++)
                await Assert.ThrowsAsync<TransientConnectorException>(() => executor.ExecuteAsync(Message(), CancellationToken.None));
        await using var harness = new ConsumerHarness(executor);
        await harness.StartAsync();
        await harness.DeliverAsync();
        Assert.Equal(expectedCalls, connector.Calls);
        harness.Channel.Verify(x => x.BasicAckAsync(42, false, It.IsAny<CancellationToken>()), ack ? Times.Once : Times.Never);
        harness.Channel.Verify(x => x.BasicNackAsync(42, false, false, It.IsAny<CancellationToken>()), ack ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task Shutdown_during_connector_execution_never_acks_or_retries()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new TestConnector("Crm", async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await using var harness = new ConsumerHarness(Executor([connector]));
        await harness.StartAsync();
        var delivery = harness.DeliverAsync();
        await entered.Task;
        await harness.StopAsync();
        await delivery;
        Assert.Equal(1, connector.Calls);
        harness.Channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Channel.Verify(x => x.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class ConsumerHarness : IAsyncDisposable
    {
        public Mock<IChannel> Channel { get; } = new();
        private readonly Worker _worker;
        private IAsyncBasicConsumer? _consumer;

        public ConsumerHarness(IConnectorExecutor executor)
        {
            var options = new RabbitMqOptions { UserName = "unit-test", Password = Guid.NewGuid().ToString() };
            Channel.SetupGet(x => x.IsOpen).Returns(true);
            Channel.Setup(x => x.BasicConsumeAsync(options.Queue, false, "", false, false, null,
                    It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
                .Callback<string, bool, string, bool, bool, IDictionary<string, object?>?, IAsyncBasicConsumer, CancellationToken>(
                    (_, _, _, _, _, _, consumer, _) => _consumer = consumer)
                .ReturnsAsync("resilience-test");
            var connection = new Mock<IConnection>();
            connection.Setup(x => x.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Channel.Object);
            var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
            factory.Setup(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
            _worker = new Worker(Options.Create(new WorkerOptions { Enabled = true }), Options.Create(options),
                new IntegrationMessageProcessor(executor, NullLogger<IntegrationMessageProcessor>.Instance),
                factory.Object, NullLogger<Worker>.Instance);
        }

        public Task StartAsync() => _worker.StartAsync(CancellationToken.None);
        public Task StopAsync() => _worker.StopAsync(CancellationToken.None);
        public Task DeliverAsync()
        {
            Assert.NotNull(_consumer);
            return _consumer.HandleBasicDeliverAsync("resilience-test", 42, false, "integrationhub.jobs",
                "integration.job.submitted", new BasicProperties(), JsonSerializer.SerializeToUtf8Bytes(Message()), CancellationToken.None);
        }
        public async ValueTask DisposeAsync() { await StopAsync(); _worker.Dispose(); }
    }
}
