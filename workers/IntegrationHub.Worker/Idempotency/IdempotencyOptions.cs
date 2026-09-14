namespace IntegrationHub.Worker.Idempotency;

public sealed class IdempotencyOptions
{
    public string Provider { get; set; } = "InMemory";
    public int RetentionHours { get; set; } = 24;
    public int LeaseSeconds { get; set; } = 120;
}
