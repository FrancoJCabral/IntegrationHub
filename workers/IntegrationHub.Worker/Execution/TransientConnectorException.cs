namespace IntegrationHub.Worker.Execution;

public sealed class TransientConnectorException(string message) : Exception(message);
