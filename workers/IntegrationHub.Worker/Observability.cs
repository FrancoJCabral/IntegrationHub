using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IntegrationHub.Worker;

public static class Observability
{
    public const string ServiceName = "IntegrationHub.Worker";
    public static readonly ActivitySource Activities = new(ServiceName);
    private static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> JobsProcessed = Meter.CreateCounter<long>("integrationhub.jobs.processed");
    public static readonly Counter<long> JobsDuplicated = Meter.CreateCounter<long>("integrationhub.jobs.duplicated");
    public static readonly Counter<long> ConnectorExecutions = Meter.CreateCounter<long>("integrationhub.connector.executions");
    public static readonly Counter<long> ConnectorFailures = Meter.CreateCounter<long>("integrationhub.connector.failures");
    public static readonly Counter<long> Retries = Meter.CreateCounter<long>("integrationhub.connector.retries");

    public static Activity? StartConsumerActivity(IDictionary<string, object?>? headers)
    {
        static string? Read(IDictionary<string, object?>? values, string key) =>
            values is not null && values.TryGetValue(key, out var value)
                ? value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string text => text, _ => null }
                : null;
        ActivityContext.TryParse(Read(headers, "traceparent"), Read(headers, "tracestate"), true, out var parent);
        return Activities.StartActivity("rabbitmq.consume", ActivityKind.Consumer, parent);
    }

    public static void AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ServiceName);
                if (configuration.GetValue("Observability:ConsoleTraces", true))
                    tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics => metrics.AddMeter(ServiceName).AddPrometheusExporter());
    }
}
