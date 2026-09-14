namespace IntegrationHub.Contracts;

public sealed record IntegrationJobResponse(
    Guid Id,
    string Connector,
    string Operation,
    string ExternalId,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    DateTime? UpdatedAtUtc);
