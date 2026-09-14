using IntegrationHub.Worker;
using IntegrationHub.Worker.Resilience;
using IntegrationHub.Worker.Messaging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddConnectorExecution(builder.Configuration);
builder.Services.AddSingleton<IntegrationMessageProcessor>();
builder.Services.AddSingleton<RabbitMQ.Client.IConnectionFactory>(services =>
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RabbitMqOptions>>().Value.CreateFactory());
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
