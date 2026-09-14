using System.Text.Json;
using IntegrationHub.Contracts;
using IntegrationHub.Worker.Execution;

namespace IntegrationHub.Worker.Messaging;

public sealed class IntegrationMessageProcessor(IConnectorExecutor executor, ILogger<IntegrationMessageProcessor> logger)
{
    // Return true only after successful execution; the consumer owns broker acknowledgements.
    public async Task<bool> ProcessAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntegrationJobSubmittedMessage? message;
        try { message = JsonSerializer.Deserialize<IntegrationJobSubmittedMessage>(body.Span); }
        catch (JsonException)
        {
            logger.LogWarning("Invalid integration message JSON; rejecting without requeue.");
            return false;
        }

        if (message is null || message.JobId == Guid.Empty ||
            message.Connector is not ("Crm" or "Erp" or "Payments") ||
            string.IsNullOrWhiteSpace(message.Operation) || message.Operation.Length > 100 ||
            string.IsNullOrWhiteSpace(message.ExternalId) || message.ExternalId.Length > 200 ||
            message.SubmittedAtUtc == default || message.SubmittedAtUtc.Kind != DateTimeKind.Utc)
        {
            logger.LogWarning("Invalid integration message structure; rejecting without requeue.");
            return false;
        }

        try
        {
            await executor.ExecuteAsync(message, cancellationToken);
            logger.LogInformation("Processed job {JobId} using {Connector}.", message.JobId, message.Connector);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogError("Processing failed for job {JobId}; rejecting without requeue.", message.JobId);
            return false;
        }
    }
}
