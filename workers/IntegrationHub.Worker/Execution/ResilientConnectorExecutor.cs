using IntegrationHub.Contracts;
using IntegrationHub.Worker.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace IntegrationHub.Worker.Execution;

public sealed class ResilientConnectorExecutor : IConnectorExecutor
{
    private static readonly ResiliencePropertyKey<Guid> JobIdKey = new("IntegrationHub.JobId");
    private readonly Dictionary<string, (IExternalConnector Connector, ResiliencePipeline Pipeline)> _connectors;
    private readonly ILogger<ResilientConnectorExecutor> _logger;

    public ResilientConnectorExecutor(IEnumerable<IExternalConnector> connectors,
        IOptions<ConnectorResilienceOptions> options, TimeProvider timeProvider,
        ILogger<ResilientConnectorExecutor> logger)
    {
        _logger = logger;
        var settings = options.Value;
        _connectors = connectors.ToDictionary(connector => connector.Name,
            connector => (connector, BuildPipeline(connector.Name, settings, timeProvider)), StringComparer.Ordinal);
    }

    public async Task ExecuteAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Connector is null || !_connectors.TryGetValue(message.Connector, out var entry))
            throw new PermanentConnectorException("Unsupported connector.");

        using var activity = Observability.Activities.StartActivity("connector.execute");
        activity?.SetTag("connector.name", message.Connector);
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(JobIdKey, message.JobId);
        try
        {
            _logger.LogInformation("Connector execution started for job {JobId} using {Connector}.",
                message.JobId, message.Connector);
            await entry.Pipeline.ExecuteAsync(
                async ctx =>
                {
                    using var attempt = Observability.Activities.StartActivity("connector.attempt");
                    Observability.ConnectorExecutions.Add(1);
                    try { await entry.Connector.ExecuteAsync(message, ctx.CancellationToken); }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        Observability.ConnectorFailures.Add(1);
                        attempt?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                        throw;
                    }
                }, context);
            _logger.LogInformation("Connector execution completed for job {JobId} using {Connector}.",
                message.JobId, message.Connector);
        }
        catch (Exception)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            throw;
        }
        finally { ResilienceContextPool.Shared.Return(context); }
    }

    private ResiliencePipeline BuildPipeline(string connector, ConnectorResilienceOptions settings, TimeProvider clock)
    {
        var circuit = settings.CircuitBreaker;
        var builder = new ResiliencePipelineBuilder { TimeProvider = clock };
        // Outermost breaker sees one final result for the entire logical operation.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = circuit.FailureRatio,
            MinimumThroughput = circuit.MinimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(circuit.SamplingDurationSeconds),
            BreakDuration = TimeSpan.FromSeconds(circuit.BreakDurationSeconds),
            ShouldHandle = args => ValueTask.FromResult(
                !args.Context.CancellationToken.IsCancellationRequested && IsTransient(args.Outcome.Exception)),
            OnOpened = args =>
            {
                _logger.LogWarning("Connector {Connector} circuit Open for {BreakDuration}.", connector, args.BreakDuration);
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = _ =>
            {
                _logger.LogInformation("Connector {Connector} circuit HalfOpen.", connector);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                _logger.LogInformation("Connector {Connector} circuit Closed.", connector);
                return ValueTask.CompletedTask;
            }
        });

        // Polly requires at least one retry in AddRetry. Zero explicitly omits this strategy.
        if (settings.MaxRetryAttempts > 0)
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = settings.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(settings.BaseDelayMilliseconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(
                    !args.Context.CancellationToken.IsCancellationRequested && IsTransient(args.Outcome.Exception)),
                OnRetry = args =>
                {
                    Observability.Retries.Add(1);
                    System.Diagnostics.Activity.Current?.AddEvent(new("connector.retry"));
                    _logger.LogWarning("Retry {Attempt} for job {JobId} using {Connector} after {Delay}; error {ErrorType}.",
                        args.AttemptNumber + 1, args.Context.Properties.GetValue(JobIdKey, Guid.Empty),
                        connector, args.RetryDelay, args.Outcome.Exception?.GetType().Name);
                    return ValueTask.CompletedTask;
                }
            });

        builder.AddTimeout(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return builder.Build();
    }

    private static bool IsTransient(Exception? exception) =>
        exception is TransientConnectorException or TimeoutRejectedException;
}
