using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IntegrationHub.Api;

public static class Observability
{
    public const string ServiceName = "IntegrationHub.Api";
    public static readonly ActivitySource Activities = new(ServiceName);
    private static readonly Meter Meter = new(ServiceName);
    public static readonly Counter<long> JobsPublished = Meter.CreateCounter<long>("integrationhub.jobs.published");

    public static void AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ServiceName).AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => context.Request.Path.StartsWithSegments("/api"));
                if (configuration.GetValue("Observability:ConsoleTraces", true))
                    tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics => metrics.AddMeter(ServiceName).AddPrometheusExporter());
    }
}
