using IntegrationHub.Worker.Execution;
using IntegrationHub.Worker.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Worker.Tests;

public sealed class ResilienceOptionsTests
{
    [Theory]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("TimeoutSeconds", "-1")]
    [InlineData("MaxRetryAttempts", "-1")]
    [InlineData("BaseDelayMilliseconds", "-1")]
    [InlineData("CircuitBreaker:FailureRatio", "0")]
    [InlineData("CircuitBreaker:FailureRatio", "1.1")]
    [InlineData("CircuitBreaker:FailureRatio", "NaN")]
    [InlineData("CircuitBreaker:MinimumThroughput", "1")]
    [InlineData("CircuitBreaker:SamplingDurationSeconds", "0")]
    [InlineData("CircuitBreaker:BreakDurationSeconds", "0")]
    public async Task Invalid_options_fail_host_startup(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Resilience:" + key] = value });
        builder.Services.AddConnectorExecution(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(error.Failures, x => x.Contains(key));
    }

    [Fact]
    public async Task Default_options_start_and_register_one_stable_executor_and_three_connectors()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddConnectorExecution(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        var first = host.Services.GetRequiredService<IConnectorExecutor>();
        Assert.Same(first, host.Services.GetRequiredService<IConnectorExecutor>());
        Assert.Equal(new[] { "Crm", "Erp", "Payments" },
            host.Services.GetServices<IExternalConnector>().Select(x => x.Name));
        await host.StopAsync();
    }
}
