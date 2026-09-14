using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationHub.Api.Infrastructure;
using IntegrationHub.Api.Infrastructure.Messaging;
using IntegrationHub.Application;
using IntegrationHub.Contracts;
using IntegrationHub.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationHub.Api.Tests;

public sealed class PublishingTests
{
    private static IntegrationJobSubmittedMessage Message() => new(Guid.NewGuid(), "Crm", "Sync", "id", DateTime.UtcNow);

    [Fact]
    public async Task In_memory_publish_stores_values_and_honors_cancellation()
    {
        var publisher = new InMemoryIntegrationJobPublisher();
        var message = Message();
        await publisher.PublishAsync(message);
        Assert.Equal(message, Assert.Single(publisher.Messages));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(Message(), cancellation.Token));
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task Post_publishes_one_normalized_message_matching_pending_response()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Messaging:Provider"] = "InMemory" })));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var before = DateTime.UtcNow;
        using var response = await client.PostAsJsonAsync("/api/integrations/jobs",
            new { connector = "Crm", operation = " Sync ", externalId = " customer-1 " });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var job = await response.Content.ReadFromJsonAsync<IntegrationJobResponse>();
        Assert.NotNull(job);
        Assert.Equal("Pending", job.Status);
        var publisher = Assert.IsType<InMemoryIntegrationJobPublisher>(factory.Services.GetRequiredService<IIntegrationJobPublisher>());
        var message = Assert.Single(publisher.Messages);
        Assert.Equal(job.Id, message.JobId);
        Assert.Equal("Crm", message.Connector);
        Assert.Equal("Sync", message.Operation);
        Assert.Equal("customer-1", message.ExternalId);
        Assert.Equal(DateTimeKind.Utc, message.SubmittedAtUtc.Kind);
        Assert.InRange(message.SubmittedAtUtc, before, DateTime.UtcNow);
        Assert.Null(factory.Services.GetService<RabbitMQ.Client.IConnectionFactory>());
    }

    [Fact]
    public async Task Failed_publication_returns_generic_500_and_retains_pending_job()
    {
        var publisher = new FailingPublisher();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIntegrationJobPublisher>();
                services.AddSingleton<IIntegrationJobPublisher>(publisher);
            }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.PostAsJsonAsync("/api/integrations/jobs",
            new { connector = "Crm", operation = "Sync", externalId = "id" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("private publication diagnostic", await response.Content.ReadAsStringAsync());
        Assert.NotNull(publisher.Message);
        Assert.Equal(1, publisher.Attempts);
        using var fetched = await client.GetAsync($"/api/integrations/jobs/{publisher.Message.JobId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        var job = await fetched.Content.ReadFromJsonAsync<IntegrationJobResponse>();
        Assert.NotNull(job);
        Assert.Equal("Pending", job.Status);
        Assert.Null(job.StartedAtUtc);
        Assert.Null(job.CompletedAtUtc);
    }

    [Fact]
    public async Task Invalid_request_does_not_publish()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var response = await client.PostAsJsonAsync("/api/integrations/jobs",
            new { connector = "Crm", operation = "", externalId = "id" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Assert.IsType<InMemoryIntegrationJobPublisher>(
            factory.Services.GetRequiredService<IIntegrationJobPublisher>()).Messages);
    }

    [Fact]
    public void Unknown_provider_is_rejected()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Messaging:Provider"] = "Typo" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddIntegrationMessaging(configuration));
    }

    [Fact]
    public void Integration_contract_contains_only_processing_fields()
    {
        var json = JsonSerializer.SerializeToElement(Message());
        string[] expected = ["JobId", "Connector", "Operation", "ExternalId", "SubmittedAtUtc"];
        Assert.Equal(expected.OrderBy(x => x), json.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }

    private sealed class FailingPublisher : IIntegrationJobPublisher
    {
        public IntegrationJobSubmittedMessage? Message { get; private set; }
        public int Attempts { get; private set; }
        public Task PublishAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            Attempts++;
            throw new InvalidOperationException("private publication diagnostic");
        }
    }
}
