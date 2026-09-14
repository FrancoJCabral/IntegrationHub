namespace IntegrationHub.Worker.Execution;

public sealed class PermanentConnectorException(string message) : Exception(message);
