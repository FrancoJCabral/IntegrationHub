using System.ComponentModel.DataAnnotations;

namespace IntegrationHub.Contracts;

public sealed record CreateIntegrationJobRequest
{
    [Required]
    public string? Connector { get; init; }

    [Required]
    [MaxLength(100)]
    public string? Operation { get; init; }

    [Required]
    [MaxLength(200)]
    public string? ExternalId { get; init; }
}
