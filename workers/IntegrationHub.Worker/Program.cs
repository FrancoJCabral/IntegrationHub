using IntegrationHub.Worker;
using IntegrationHub.Worker.Resilience;
using IntegrationHub.Worker.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddObservability(builder.Configuration);
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://localhost:9464");
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddConnectorExecution(builder.Configuration);
builder.Services.AddSingleton<IntegrationMessageProcessor>();
builder.Services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(services =>
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RabbitMqOptions>>().Value.CreateFactory());
builder.Services.AddHostedService<Worker>();
var app = builder.Build();
app.MapPrometheusScrapingEndpoint();
await app.RunAsync();
