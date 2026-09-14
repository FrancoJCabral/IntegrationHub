namespace IntegrationHub.Worker.Resilience;

public sealed class ConnectorResilienceOptions
{
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 3;
    public int BaseDelayMilliseconds { get; set; } = 200;
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

public sealed class CircuitBreakerOptions
{
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 4;
    public int SamplingDurationSeconds { get; set; } = 30;
    public int BreakDurationSeconds { get; set; } = 15;
}
