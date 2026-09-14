using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationHub.Api.Tests;

// xUnit creates a new test class (and therefore a new host/repository) for every test case.
public sealed class IntegrationJobsApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
    private readonly HttpClient _client;

    public IntegrationJobsApiTests() => _client = Client(_factory);
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });
    private Task<HttpResponseMessage> Create() => _client.PostAsJsonAsync("/api/integrations/jobs",
        new { connector = "Crm", operation = "SyncCustomer", externalId = "customer-123" });
    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Health_returns_200() => Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/health")).StatusCode);

    [Fact]
    public async Task Create_valid_job_returns_202() => Assert.Equal(HttpStatusCode.Accepted, (await Create()).StatusCode);

    [Fact]
    public async Task Create_valid_job_returns_location_header()
    {
        using var response = await Create();
        var body = await Body(response);
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/integrations/jobs/{body.GetProperty("id").GetGuid()}", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Create_valid_job_returns_pending_status()
    {
        var body = await Body(await Create());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        foreach (var name in new[] { "startedAtUtc", "completedAtUtc", "updatedAtUtc" })
            Assert.Equal(JsonValueKind.Null, body.GetProperty(name).ValueKind);
        Assert.Equal(DateTimeKind.Utc, body.GetProperty("createdAtUtc").GetDateTime().Kind);
    }

    [Fact]
    public async Task Create_then_get_returns_same_job()
    {
        using var created = await Create();
        using var fetched = await _client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await fetched.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_unknown_job_returns_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/integrations/jobs/{Guid.NewGuid()}")).StatusCode);

    [Theory]
    [InlineData("operation", "")]
    [InlineData("operation", " \t ")]
    [InlineData("operation", null)]
    [InlineData("externalId", "")]
    [InlineData("externalId", " \t ")]
    [InlineData("externalId", null)]
    [InlineData("connector", "Unknown")]
    [InlineData("connector", "0")]
    [InlineData("connector", "Crm, Erp")]
    [InlineData("connector", "")]
    [InlineData("connector", null)]
    public async Task Invalid_field_returns_400(string field, string? value)
    {
        var request = new Dictionary<string, string?> { ["connector"] = "Crm", ["operation"] = "Sync", ["externalId"] = "id" };
        request[field] = value;
        using var response = await _client.PostAsJsonAsync("/api/integrations/jobs", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("operation", 101)]
    [InlineData("externalId", 201)]
    public async Task Oversized_field_returns_400(string field, int length)
    {
        var request = new Dictionary<string, string> { ["connector"] = "Crm", ["operation"] = "Sync", ["externalId"] = "id" };
        request[field] = new string('a', length);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/integrations/jobs", request)).StatusCode);
    }

    [Theory]
    [InlineData("{\"connector\":0,\"operation\":\"Sync\",\"externalId\":\"id\"}")]
    [InlineData("{}")]
    [InlineData("{broken")]
    public async Task Invalid_json_or_missing_fields_returns_400(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/integrations/jobs", content)).StatusCode);
    }

    [Theory]
    [InlineData("connector", "Crm")]
    [InlineData("status", "Pending")]
    public async Task Response_serializes_enum_as_string(string field, string expected)
    {
        var body = await Body(await Create());
        Assert.Equal(JsonValueKind.String, body.GetProperty(field).ValueKind);
        Assert.Equal(expected, body.GetProperty(field).GetString());
    }

    [Fact]
    public async Task Response_does_not_expose_internal_fields()
    {
        var body = await Body(await Create());
        string[] expected = ["id", "connector", "operation", "externalId", "status", "createdAtUtc", "startedAtUtc", "completedAtUtc", "updatedAtUtc"];
        Assert.Equal(expected.OrderBy(x => x), body.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }

    [Theory]
    [InlineData("Erp")]
    [InlineData("Payments")]
    public async Task Other_connectors_and_trim_are_supported(string connector)
    {
        var response = await _client.PostAsJsonAsync("/api/integrations/jobs", new { connector, operation = " Sync ", externalId = " id " });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await Body(response);
        Assert.Equal(connector, body.GetProperty("connector").GetString());
        Assert.Equal("Sync", body.GetProperty("operation").GetString());
        Assert.Equal("id", body.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task Separate_hosts_do_not_share_repository()
    {
        using var created = await Create();
        using var otherFactory = new WebApplicationFactory<Program>();
        using var otherClient = Client(otherFactory);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync(created.Headers.Location)).StatusCode);
    }

    [Theory]
    [InlineData("Production", HttpStatusCode.NotFound)]
    [InlineData("Development", HttpStatusCode.OK)]
    public async Task Swagger_is_development_only(string environment, HttpStatusCode expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = Client(factory);
        Assert.Equal(expected, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Unexpected_error_returns_generic_problem_details(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(environment).ConfigureServices(services =>
            {
                services.RemoveAll<IIntegrationJobRepository>();
                services.AddSingleton<IIntegrationJobRepository, ThrowingRepository>();
            }));
        using var client = Client(factory);
        using var response = await client.GetAsync($"/api/integrations/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private diagnostic", text);
        Assert.DoesNotContain("stackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("An unexpected error occurred.", (await Body(response)).GetProperty("title").GetString());
    }

    private sealed class ThrowingRepository : IIntegrationJobRepository
    {
        public Task AddAsync(IntegrationJob job, CancellationToken cancellationToken = default) => throw new InvalidOperationException("private diagnostic");
        public Task<IntegrationJob?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("private diagnostic");
    }

    public void Dispose() { _client.Dispose(); _factory.Dispose(); }
}
