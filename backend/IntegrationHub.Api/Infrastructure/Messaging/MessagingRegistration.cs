using IntegrationHub.Application;

namespace IntegrationHub.Api.Infrastructure.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddIntegrationMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        switch (configuration["Messaging:Provider"] ?? "InMemory")
        {
            case "InMemory":
                services.AddSingleton<IIntegrationJobPublisher, InMemoryIntegrationJobPublisher>();
                break;
            case "RabbitMq":
                var options = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new();
                options.Validate();
                services.AddSingleton(options);
                services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(_ => options.CreateFactory());
                services.AddSingleton<IIntegrationJobPublisher, RabbitMqIntegrationJobPublisher>();
                break;
            default:
                throw new InvalidOperationException("Messaging:Provider must be InMemory or RabbitMq.");
        }
        return services;
    }
}
