using System.Collections.Concurrent;
using IntegrationHub.Application;
using IntegrationHub.Contracts;

namespace IntegrationHub.Api.Infrastructure.Messaging;

public sealed class InMemoryIntegrationJobPublisher : IIntegrationJobPublisher
{
    private readonly ConcurrentQueue<IntegrationJobSubmittedMessage> _messages = new();

    public IReadOnlyCollection<IntegrationJobSubmittedMessage> Messages => _messages.ToArray();

    public Task PublishAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        using var activity = Observability.Activities.StartActivity("inmemory.publish", System.Diagnostics.ActivityKind.Producer);
        _messages.Enqueue(message);
        Observability.JobsPublished.Add(1);
        return Task.CompletedTask;
    }
}
