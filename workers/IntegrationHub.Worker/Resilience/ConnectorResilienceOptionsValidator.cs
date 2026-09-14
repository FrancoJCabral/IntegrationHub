using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Resilience;

public sealed class ConnectorResilienceOptionsValidator : IValidateOptions<ConnectorResilienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ConnectorResilienceOptions options)
    {
        var failures = new List<string>();
        if (options.TimeoutSeconds is < 1 or > 86400)
            failures.Add("Resilience:TimeoutSeconds must be between 1 and 86400.");
        if (options.MaxRetryAttempts < 0)
            failures.Add("Resilience:MaxRetryAttempts must not be negative.");
        if (options.BaseDelayMilliseconds is < 0 or > 86400000)
            failures.Add("Resilience:BaseDelayMilliseconds must be between 0 and 86400000.");
        var circuit = options.CircuitBreaker;
        if (circuit is null)
            failures.Add("Resilience:CircuitBreaker is required.");
        else
        {
            if (!double.IsFinite(circuit.FailureRatio) || circuit.FailureRatio <= 0 || circuit.FailureRatio > 1)
                failures.Add("Resilience:CircuitBreaker:FailureRatio must be greater than 0 and at most 1.");
            if (circuit.MinimumThroughput < 2)
                failures.Add("Resilience:CircuitBreaker:MinimumThroughput must be at least 2.");
            if (circuit.SamplingDurationSeconds is < 1 or > 86400)
                failures.Add("Resilience:CircuitBreaker:SamplingDurationSeconds must be between 1 and 86400.");
            if (circuit.BreakDurationSeconds is < 1 or > 86400)
                failures.Add("Resilience:CircuitBreaker:BreakDurationSeconds must be between 1 and 86400.");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
