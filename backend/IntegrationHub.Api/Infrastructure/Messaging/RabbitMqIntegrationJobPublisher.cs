using System.Text.Json;
using IntegrationHub.Application;
using IntegrationHub.Contracts;
using RabbitMQ.Client;

namespace IntegrationHub.Api.Infrastructure.Messaging;

// A single gate protects channel creation and publishing: IChannel must not be shared concurrently.
public sealed class RabbitMqIntegrationJobPublisher(RabbitMqOptions options, IConnectionFactory connectionFactory) : IIntegrationJobPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public async Task PublishAsync(IntegrationJobSubmittedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channel is null || !_channel.IsOpen || _connection is null || !_connection.IsOpen)
            {
                await ReleaseResourcesAsync();
                options.Validate();
                _connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
                _channel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    cancellationToken);
                await _channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Direct, durable: true,
                    autoDelete: false, cancellationToken: cancellationToken);
                // Declare the same durable binding as the worker so messages can wait before it starts.
                await _channel.QueueDeclareAsync(options.Queue, durable: true, exclusive: false,
                    autoDelete: false, cancellationToken: cancellationToken);
                await _channel.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey,
                    cancellationToken: cancellationToken);
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var properties = new BasicProperties { ContentType = "application/json", Persistent = true };
            // mandatory + tracked confirms makes unroutable/negatively acknowledged publications fail.
            await _channel.BasicPublishAsync(options.Exchange, options.RoutingKey, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        catch
        {
            // A later request can establish a fresh channel; this request is never retried.
            await ReleaseResourcesAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ReleaseResourcesAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try { if (channel is not null) await channel.DisposeAsync(); }
        finally { if (connection is not null) await connection.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await ReleaseResourcesAsync();
        }
        finally { _gate.Release(); }
    }
}
