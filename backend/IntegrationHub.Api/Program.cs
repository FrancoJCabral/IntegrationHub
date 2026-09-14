using IntegrationHub.Api.Infrastructure;
using IntegrationHub.Application;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IIntegrationJobRepository, InMemoryIntegrationJobRepository>();
builder.Services.AddScoped<IIntegrationJobService, IntegrationJobService>();

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    await Results.Problem(statusCode: 500, title: "An unexpected error occurred.").ExecuteAsync(context)));
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();
app.MapGet("/api/health", () => Results.Ok());
app.MapControllers();
app.Run();

public partial class Program;
