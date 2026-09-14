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

public sealed class WorkerTests
{
    private static IntegrationJobSubmittedMessage Message(string connector = "Crm") =>
        new(Guid.NewGuid(), connector, "Sync", "id", DateTime.UtcNow);

    [Theory]
    [InlineData("Crm")]
    [InlineData("Erp")]
    [InlineData("Payments")]
    public async Task Simulated_connector_succeeds_without_mutating_message(string connector)
    {
        var message = Message(connector);
        var original = message with { };
        await new SimulatedExternalConnector(connector).ExecuteAsync(message, CancellationToken.None);
        Assert.Equal(original, message);
    }

    [Fact]
    public async Task Unknown_connector_is_rejected() =>
        await Assert.ThrowsAsync<PermanentConnectorException>(() =>
            new SimulatedExternalConnector("Unknown").ExecuteAsync(Message("Unknown"), CancellationToken.None));

    [Fact]
    public async Task Cancellation_is_honored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SimulatedExternalConnector("Crm").ExecuteAsync(Message(), cancellation.Token));
    }

    [Fact]
    public async Task Disabled_worker_starts_and_stops_without_calling_broker_factory()
    {
        var factory = new Mock<IConnectionFactory>(MockBehavior.Strict);
        var executor = new Mock<IConnectorExecutor>(MockBehavior.Strict);
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        using var worker = new Worker(Options.Create(new WorkerOptions { Enabled = false }),
            Options.Create(new RabbitMqOptions()), processor, factory.Object, NullLogger<Worker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask;
        await worker.StopAsync(CancellationToken.None);
        factory.VerifyNoOtherCalls();
        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Processor_returns_success_only_after_execution_finishes()
    {
        var executor = new Mock<IConnectorExecutor>(MockBehavior.Strict);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = Message();
        executor.Setup(x => x.ExecuteAsync(message, It.IsAny<CancellationToken>())).Returns(completed.Task);
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        var processing = processor.ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(message), CancellationToken.None);
        Assert.False(processing.IsCompleted);
        completed.SetResult();
        Assert.True(await processing);
        executor.Verify(x => x.ExecuteAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Invalid_json_or_missing_fields_is_rejected_without_execution(string json)
    {
        var executor = new Mock<IConnectorExecutor>(MockBehavior.Strict);
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        Assert.False(await processor.ProcessAsync(Encoding.UTF8.GetBytes(json), CancellationToken.None));
        executor.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("connector")]
    [InlineData("id")]
    [InlineData("operation")]
    [InlineData("externalId")]
    [InlineData("longOperation")]
    [InlineData("longExternalId")]
    [InlineData("timestamp")]
    public async Task Invalid_structure_is_rejected(string field)
    {
        var message = Message();
        message = field switch
        {
            "connector" => message with { Connector = "Unknown" },
            "id" => message with { JobId = Guid.Empty },
            "operation" => message with { Operation = " " },
            "externalId" => message with { ExternalId = "" },
            "longOperation" => message with { Operation = new string('x', 101) },
            "longExternalId" => message with { ExternalId = new string('x', 201) },
            _ => message with { SubmittedAtUtc = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified) }
        };
        var executor = new Mock<IConnectorExecutor>(MockBehavior.Strict);
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        Assert.False(await processor.ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(message), CancellationToken.None));
        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unexpected_executor_failure_is_rejected_without_retry()
    {
        var executor = new Mock<IConnectorExecutor>();
        executor.Setup(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private diagnostic"));
        var processor = new IntegrationMessageProcessor(executor.Object, NullLogger<IntegrationMessageProcessor>.Instance);
        Assert.False(await processor.ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(Message()), CancellationToken.None));
        executor.Verify(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_reported_as_message_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var processor = new IntegrationMessageProcessor(new Mock<IConnectorExecutor>(MockBehavior.Strict).Object,
            NullLogger<IntegrationMessageProcessor>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(Message()), cancellation.Token));
    }
}
