using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static IntegrationHub.Worker.Tests.ResilienceTestSupport;

namespace IntegrationHub.Worker.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class ObservabilityTests
{
    [Fact]
    public async Task Retry_success_and_duplicate_record_distinct_metrics_and_correlated_spans()
    {
        var counts = new Dictionary<string, long>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Observability.ServiceName)
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Assert.True(tags.IsEmpty); // No JobId, payload or other unbounded metric labels.
            counts[instrument.Name] = counts.GetValueOrDefault(instrument.Name) + value;
        });
        meter.Start();
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Observability.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);
        var parent = new Activity("publisher").SetIdFormat(ActivityIdFormat.W3C).Start();
        var parentId = parent.Id!;
        var traceId = parent.TraceId;
        var spanId = parent.SpanId;
        parent.Stop();
        var headers = new Dictionary<string, object?> { ["traceparent"] = Encoding.UTF8.GetBytes(parentId) };
        var connector = new TestConnector("Crm", (call, _) => call == 1
            ? Task.FromException(new TransientConnectorException("private diagnostic"))
            : Task.CompletedTask);
        var options = Options.Create(new IdempotencyOptions());
        var executor = new IdempotentConnectorExecutor(Executor([connector]),
            new InMemoryIdempotencyStore(options, TimeProvider.System), options, TimeProvider.System,
            NullLogger<IdempotentConnectorExecutor>.Instance);
        var message = Message();
        using (Observability.StartConsumerActivity(headers))
            await executor.ExecuteAsync(message, CancellationToken.None);
        using (Observability.StartConsumerActivity(headers))
            await executor.ExecuteAsync(message, CancellationToken.None);

        Assert.Equal(2, counts["integrationhub.connector.executions"]);
        Assert.Equal(1, counts["integrationhub.connector.failures"]);
        Assert.Equal(1, counts["integrationhub.connector.retries"]);
        Assert.Equal(1, counts["integrationhub.jobs.processed"]);
        Assert.Equal(1, counts["integrationhub.jobs.duplicated"]);
        Assert.All(spans, span => Assert.Equal(traceId, span.TraceId));
        var consumers = spans.Where(span => span.Kind == ActivityKind.Consumer).ToArray();
        Assert.Equal(2, consumers.Length);
        Assert.All(consumers, span => Assert.Equal(spanId, span.ParentSpanId));
        Assert.Contains(spans, span => span.Events.Any(e => e.Name == "connector.retry"));
        Assert.Contains(spans, span => span.Events.Any(e => e.Name == "idempotency.duplicate"));
        Assert.DoesNotContain(spans.SelectMany(span => span.Tags), tag => tag.Value?.Contains("private diagnostic") == true);
    }
}
