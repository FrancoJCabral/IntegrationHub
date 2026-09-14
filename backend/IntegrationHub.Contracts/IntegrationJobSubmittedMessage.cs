namespace IntegrationHub.Contracts;

public sealed record IntegrationJobSubmittedMessage(
    Guid JobId,
    string Connector,
    string Operation,
    string ExternalId,
    DateTime SubmittedAtUtc);
