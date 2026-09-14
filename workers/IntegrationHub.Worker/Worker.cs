using IntegrationHub.Worker.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace IntegrationHub.Worker;

public sealed class Worker(
    IOptions<WorkerOptions> workerOptions,
    IOptions<RabbitMqOptions> rabbitOptions,
    IntegrationMessageProcessor processor,
    IConnectionFactory connectionFactory,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            logger.LogInformation("Integration worker disabled; no RabbitMQ connection will be created.");
            return;
        }

        var options = rabbitOptions.Value;
        options.Validate();
        try
        {
            await using var connection = await connectionFactory.CreateConnectionAsync(stoppingToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Direct, durable: true,
                autoDelete: false, cancellationToken: stoppingToken);
            await channel.QueueDeclareAsync(options.Queue, durable: true, exclusive: false,
                autoDelete: false, cancellationToken: stoppingToken);
            await channel.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey,
                cancellationToken: stoppingToken);
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false,
                cancellationToken: stoppingToken);

            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ConnectionShutdownAsync += (_, _) =>
            {
                if (!stoppingToken.IsCancellationRequested)
                    failure.TrySetException(new InvalidOperationException("RabbitMQ connection closed."));
                return Task.CompletedTask;
            };
            channel.ChannelShutdownAsync += (_, _) =>
            {
                if (!stoppingToken.IsCancellationRequested)
                    failure.TrySetException(new InvalidOperationException("RabbitMQ channel closed."));
                return Task.CompletedTask;
            };

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                try
                {
                    using var activity = Observability.StartConsumerActivity(delivery.BasicProperties.Headers);
                    activity?.SetTag("messaging.system", "rabbitmq");
                    activity?.SetTag("messaging.destination.name", options.Queue);
                    // The body is fully consumed before this callback returns.
                    var succeeded = await processor.ProcessAsync(delivery.Body, stoppingToken);
                    if (!succeeded) activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                    if (succeeded)
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    else
                        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false,
                            cancellationToken: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Leave in-flight work unacknowledged. Closing the channel returns it to the broker.
                }
                catch (Exception)
                {
                    failure.TrySetException(new InvalidOperationException("RabbitMQ delivery acknowledgement failed."));
                }
            };
            consumer.UnregisteredAsync += (_, _) =>
            {
                if (!stoppingToken.IsCancellationRequested)
                    failure.TrySetException(new InvalidOperationException("RabbitMQ consumer was cancelled."));
                return Task.CompletedTask;
            };

            var consumerTag = await channel.BasicConsumeAsync(options.Queue, autoAck: false, consumer: consumer,
                cancellationToken: stoppingToken);
            logger.LogInformation("Integration worker consuming with prefetch 10.");
            try { await failure.Task.WaitAsync(stoppingToken); }
            finally
            {
                if (channel.IsOpen)
                    await channel.BasicCancelAsync(consumerTag, cancellationToken: CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Do not log connection exception details: they can contain deployment information.
            logger.LogError("RabbitMQ worker stopped after an infrastructure failure.");
            throw new InvalidOperationException("RabbitMQ worker infrastructure failure.");
        }
    }
}
