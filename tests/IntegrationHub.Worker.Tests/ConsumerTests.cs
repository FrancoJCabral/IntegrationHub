using System.Text;
using System.Text.Json;
using IntegrationHub.Contracts;
using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RabbitMQ.Client;

namespace IntegrationHub.Worker.Tests;

public sealed class ConsumerTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("invalid")]
    [InlineData("failure")]
    public async Task Consumer_acks_only_completed_work_and_nacks_failures_without_requeue(string scenario)
    {
        var options = new RabbitMqOptions { UserName = "unit-test", Password = Guid.NewGuid().ToString() };
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        IAsyncBasicConsumer? consumer = null;
        channel.Setup(x => x.BasicConsumeAsync(options.Queue, false, "", false, false, null,
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .Callback<string, bool, string, bool, bool, IDictionary<string, object?>?, IAsyncBasicConsumer, CancellationToken>(
                (_, _, _, _, _, _, value, _) => consumer = value)
            .ReturnsAsync("consumer-test");
        var connection = new Mock<IConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
        factory.Setup(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Mock<IConnectorExecutor>();
        executor.Setup(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(execution.Task);
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        using var worker = new Worker(Options.Create(new WorkerOptions { Enabled = true }), Options.Create(options),
            processor, factory.Object, NullLogger<Worker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.NotNull(consumer);
            channel.Verify(x => x.BasicQosAsync(0, 10, false, It.IsAny<CancellationToken>()), Times.Once);
            var body = scenario == "invalid" ? Encoding.UTF8.GetBytes("{broken") : JsonSerializer.SerializeToUtf8Bytes(
                new IntegrationJobSubmittedMessage(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow));
            var delivery = consumer.HandleBasicDeliverAsync("consumer-test", 42, false, options.Exchange,
                options.RoutingKey, new BasicProperties(), body, CancellationToken.None);
            channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            if (scenario == "failure") execution.SetException(new InvalidOperationException("simulated execution failure"));
            else execution.SetResult();
            await delivery;
            if (scenario == "success")
            {
                channel.Verify(x => x.BasicAckAsync(42, false, It.IsAny<CancellationToken>()), Times.Once);
                channel.Verify(x => x.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            else
            {
                channel.Verify(x => x.BasicNackAsync(42, false, false, It.IsAny<CancellationToken>()), Times.Once);
                channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
                Assert.False(worker.ExecuteTask!.IsCompleted);
            }
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        channel.Verify(x => x.BasicCancelAsync("consumer-test", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.DisposeAsync(), Times.Once);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }
}
