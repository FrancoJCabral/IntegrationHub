using RabbitMQ.Client;

namespace IntegrationHub.Worker.Messaging;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";
    public string Exchange { get; set; } = "integrationhub.jobs";
    public string Queue { get; set; } = "integrationhub.worker";
    public string RoutingKey { get; set; } = "integration.job.submitted";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(HostName) || Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password) ||
            string.IsNullOrWhiteSpace(VirtualHost) || string.IsNullOrWhiteSpace(Exchange) ||
            string.IsNullOrWhiteSpace(Queue) || string.IsNullOrWhiteSpace(RoutingKey))
            throw new InvalidOperationException("RabbitMq configuration is incomplete. Supply credentials through environment variables or User Secrets.");
    }

    public ConnectionFactory CreateFactory() => new()
    {
        HostName = HostName, Port = Port, UserName = UserName, Password = Password, VirtualHost = VirtualHost,
        AutomaticRecoveryEnabled = false,
        ConsumerDispatchConcurrency = 1
    };
}
