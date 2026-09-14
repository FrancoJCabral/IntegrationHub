using IntegrationHub.Contracts;
using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Idempotency;
using IntegrationHub.Worker.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;
using System.Text.Json;

namespace IntegrationHub.Worker.Tests;

public sealed class IdempotencyTests
{
    private static IntegrationJobSubmittedMessage Message() => new(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow);
    private static IntegrationMessageProcessor Processor(IConnectorExecutor inner, IIdempotencyStore store) => new(
        new IdempotentConnectorExecutor(inner, store, Options.Create(new IdempotencyOptions()), TimeProvider.System,
            NullLogger<IdempotentConnectorExecutor>.Instance), NullLogger<IntegrationMessageProcessor>.Instance);
    private static InMemoryIdempotencyStore Store() => new(Options.Create(new IdempotencyOptions()), TimeProvider.System);

    [Fact]
    public async Task First_executes_and_completed_duplicate_returns_ack_path_without_executing()
    {
        var inner = new Mock<IConnectorExecutor>();
        var processor = Processor(inner.Object, Store());
        var body = JsonSerializer.SerializeToUtf8Bytes(Message());
        Assert.True(await processor.ProcessAsync(body, CancellationToken.None));
        Assert.True(await processor.ProcessAsync(body, CancellationToken.None));
        inner.Verify(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Failure_does_not_complete_and_same_job_can_succeed_later()
    {
        var inner = new Mock<IConnectorExecutor>();
        inner.SetupSequence(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermanentConnectorException("controlled failure"))
            .Returns(Task.CompletedTask);
        var processor = Processor(inner.Object, Store());
        var body = JsonSerializer.SerializeToUtf8Bytes(Message());
        Assert.False(await processor.ProcessAsync(body, CancellationToken.None));
        Assert.True(await processor.ProcessAsync(body, CancellationToken.None));
        Assert.True(await processor.ProcessAsync(body, CancellationToken.None));
        inner.Verify(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Concurrent_duplicates_allow_only_one_execution_owner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Mock<IConnectorExecutor>();
        inner.Setup(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return finish.Task; });
        var store = Store();
        var firstProcessor = Processor(inner.Object, store);
        var secondProcessor = Processor(inner.Object, store);
        var body = JsonSerializer.SerializeToUtf8Bytes(Message());
        var first = firstProcessor.ProcessAsync(body, CancellationToken.None);
        await entered.Task;
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            secondProcessor.ProcessAsync(body, CancellationToken.None))));
        Assert.All(duplicates, Assert.True);
        finish.SetResult();
        Assert.True(await first);
        inner.Verify(x => x.ExecuteAsync(It.IsAny<IntegrationJobSubmittedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Expired_owner_cannot_complete_or_release_new_owner_and_completion_expires()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryIdempotencyStore(Options.Create(new IdempotencyOptions { LeaseSeconds = 5, RetentionHours = 1 }), clock);
        var id = Guid.NewGuid();
        Assert.True(await store.TryAcquireAsync(id, "old", CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await store.TryAcquireAsync(id, "new", CancellationToken.None));
        Assert.False(await store.CompleteAsync(id, "old", CancellationToken.None));
        await store.ReleaseAsync(id, "old", CancellationToken.None);
        Assert.False(await store.TryAcquireAsync(id, "third", CancellationToken.None));
        Assert.True(await store.CompleteAsync(id, "new", CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.TryAcquireAsync(id, "later", CancellationToken.None));
    }

    [Fact]
    public void Default_provider_does_not_register_or_connect_to_redis()
    {
        var services = new ServiceCollection();
        services.AddIdempotency(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryIdempotencyStore>(provider.GetRequiredService<IIdempotencyStore>());
        Assert.Null(provider.GetService<IConnectionMultiplexer>());
        Assert.Null(provider.GetService<IDatabase>());
    }
}
