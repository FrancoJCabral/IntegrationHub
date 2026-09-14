using System.Diagnostics;
using System.Text.Json;
using IntegrationHub.Api.Infrastructure.Messaging;
using IntegrationHub.Contracts;
using Moq;
using RabbitMQ.Client;

namespace IntegrationHub.Api.Tests;

public sealed class RabbitMqPublisherTests
{
    [Fact]
    public async Task Publisher_reuses_connection_declares_durable_topology_and_waits_for_confirmation()
    {
        var options = new RabbitMqOptions { UserName = "unit-test", Password = Guid.NewGuid().ToString() };
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        var connection = new Mock<IConnection>();
        connection.SetupGet(x => x.IsOpen).Returns(true);
        CreateChannelOptions? channelOptions = null;
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .Callback<CreateChannelOptions, CancellationToken>((value, _) => channelOptions = value)
            .ReturnsAsync(channel.Object);
        var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
        factory.Setup(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        using var parent = new Activity("http-request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BasicProperties? properties = null;
        IntegrationJobSubmittedMessage? published = null;
        channel.Setup(x => x.BasicPublishAsync(options.Exchange, options.RoutingKey, true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, bytes, _) =>
                {
                    properties = props;
                    published = JsonSerializer.Deserialize<IntegrationJobSubmittedMessage>(bytes.Span);
                })
            .Returns(() => new ValueTask(confirmation.Task));

        await using (var publisher = new RabbitMqIntegrationJobPublisher(options, factory.Object))
        {
            var message = new IntegrationJobSubmittedMessage(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow);
            var publication = publisher.PublishAsync(message);
            Assert.False(publication.IsCompleted);
            Assert.Equal(message, published);
            Assert.NotNull(properties);
            Assert.True(properties.Persistent);
            Assert.NotNull(properties.Headers);
            Assert.True(ActivityContext.TryParse((string)properties.Headers["traceparent"]!, null, out var propagated));
            Assert.Equal(parent.TraceId, propagated.TraceId);
            Assert.Equal("application/json", properties.ContentType);
            Assert.NotNull(channelOptions);
            Assert.True(channelOptions.PublisherConfirmationsEnabled);
            Assert.True(channelOptions.PublisherConfirmationTrackingEnabled);
            confirmation.SetResult();
            await publication;
            await publisher.PublishAsync(message);
            factory.Verify(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>()), Times.Once);
            channel.Verify(x => x.ExchangeDeclareAsync(options.Exchange, ExchangeType.Direct, true, false,
                null, false, false, It.IsAny<CancellationToken>()), Times.Once);
            channel.Verify(x => x.QueueDeclareAsync(options.Queue, true, false, false, null, false, false,
                It.IsAny<CancellationToken>()), Times.Once);
            channel.Verify(x => x.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey, null,
                false, It.IsAny<CancellationToken>()), Times.Once);
        }
        channel.Verify(x => x.DisposeAsync(), Times.Once);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Publication_failure_propagates_without_retry_and_releases_resources()
    {
        var options = new RabbitMqOptions { UserName = "unit-test", Password = Guid.NewGuid().ToString() };
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.BasicPublishAsync(It.IsAny<string>(), It.IsAny<string>(), true,
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask(Task.FromException(new InvalidOperationException("confirmation failed"))));
        var connection = new Mock<IConnection>();
        connection.SetupGet(x => x.IsOpen).Returns(true);
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
        factory.Setup(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(connection.Object);
        await using var publisher = new RabbitMqIntegrationJobPublisher(options, factory.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(
            new(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow)));
        factory.Verify(x => x.CreateConnectionAsync(It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.DisposeAsync(), Times.Once);
        connection.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Cancelled_publication_does_not_connect()
    {
        var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
        await using var publisher = new RabbitMqIntegrationJobPublisher(new(), factory.Object);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(
            new(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow), cancellation.Token));
        factory.VerifyNoOtherCalls();
    }
}
