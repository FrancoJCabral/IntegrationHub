using IntegrationHub.Worker.Execution;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Resilience;

public static class ConnectorRegistration
{
    public static IServiceCollection AddConnectorExecution(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ConnectorResilienceOptions>, ConnectorResilienceOptionsValidator>();
        services.AddOptions<ConnectorResilienceOptions>()
            .Bind(configuration.GetSection("Resilience"))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        foreach (var name in new[] { "Crm", "Erp", "Payments" })
            services.AddSingleton<IExternalConnector>(new SimulatedExternalConnector(name));
        services.AddSingleton<IConnectorExecutor, ResilientConnectorExecutor>();
        return services;
    }
}
