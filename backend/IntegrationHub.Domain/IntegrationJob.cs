namespace IntegrationHub.Domain;

public sealed class IntegrationJob
{
    public const int MaxOperationLength = 100;
    public const int MaxExternalIdLength = 200;

    public Guid Id { get; } = Guid.NewGuid();
    public ConnectorType Connector { get; }
    public string Operation { get; }
    public string ExternalId { get; }
    public IntegrationJobStatus Status { get; private set; } = IntegrationJobStatus.Pending;
    public DateTime CreatedAtUtc { get; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    public IntegrationJob(ConnectorType connector, string operation, string externalId)
    {
        if (!Enum.IsDefined(connector))
            throw new ArgumentOutOfRangeException(nameof(connector), "Connector must be a defined value.");

        Connector = connector;
        Operation = NormalizeRequired(operation, MaxOperationLength, nameof(operation));
        ExternalId = NormalizeRequired(externalId, MaxExternalIdLength, nameof(externalId));
        CreatedAtUtc = DateTime.UtcNow;
    }

    public void MarkProcessing()
    {
        RequireStatus(IntegrationJobStatus.Pending);
        var now = DateTime.UtcNow;
        Status = IntegrationJobStatus.Processing;
        StartedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void MarkSucceeded() => Complete(IntegrationJobStatus.Succeeded);

    public void MarkFailed() => Complete(IntegrationJobStatus.Failed);

    private void Complete(IntegrationJobStatus status)
    {
        RequireStatus(IntegrationJobStatus.Processing);
        var now = DateTime.UtcNow;
        Status = status;
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    private void RequireStatus(IntegrationJobStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Job must be {expected} for this transition.");
    }

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        return normalized;
    }
}
