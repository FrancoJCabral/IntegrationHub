using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IntegrationHub.Worker.Idempotency;

public static class IdempotencyRegistration
{
    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection("Idempotency"))
            .Validate(options => options.Provider is "InMemory" or "Redis", "Idempotency:Provider must be InMemory or Redis.")
            .Validate(options => options.RetentionHours is >= 1 and <= 8760, "Idempotency:RetentionHours must be 1..8760.")
            .Validate(options => options.LeaseSeconds is >= 5 and <= 3600, "Idempotency:LeaseSeconds must be 5..3600.")
            .ValidateOnStart();
        if (configuration["Idempotency:Provider"] == "Redis")
        {
            // Lazy by DI: disabled Worker and InMemory never open a Redis connection.
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var connectionOptions = ConfigurationOptions.Parse(configuration["Redis:Configuration"] ?? "localhost:6379");
                connectionOptions.AbortOnConnectFail = true;
                return ConnectionMultiplexer.Connect(connectionOptions);
            });
            services.AddSingleton<IDatabase>(provider => provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase());
            services.AddSingleton<IIdempotencyStore>(provider => new RedisIdempotencyStore(
                () => provider.GetRequiredService<IDatabase>(), provider.GetRequiredService<IOptions<IdempotencyOptions>>()));
        }
        else services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }
}
