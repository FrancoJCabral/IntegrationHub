using IntegrationHub.Application;
using IntegrationHub.Contracts;
using IntegrationHub.Domain;
using Microsoft.AspNetCore.Mvc;

namespace IntegrationHub.Api.Controllers;

[ApiController]
[Route("api/integrations/jobs")]
public sealed class IntegrationJobsController(IIntegrationJobService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(IntegrationJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IntegrationJobResponse>> Create(
        CreateIntegrationJobRequest request, CancellationToken cancellationToken)
    {
        // Required fields have already passed ApiController validation.
        var operation = request.Operation!.Trim();
        var externalId = request.ExternalId!.Trim();
        var validConnector = Enum.TryParse<ConnectorType>(request.Connector, true, out var connector)
            && Enum.GetNames<ConnectorType>().Contains(request.Connector, StringComparer.OrdinalIgnoreCase);
        if (!validConnector)
            ModelState.AddModelError("connector", "Connector must be Crm, Erp or Payments.");
        if (operation.Length > IntegrationJob.MaxOperationLength)
            ModelState.AddModelError("operation", "Operation must not exceed 100 characters after trimming.");
        if (externalId.Length > IntegrationJob.MaxExternalIdLength)
            ModelState.AddModelError("externalId", "ExternalId must not exceed 200 characters after trimming.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var job = await service.CreateAsync(connector, operation, externalId, cancellationToken);
        return AcceptedAtAction(nameof(GetById), new { id = job.Id }, ToResponse(job));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(IntegrationJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IntegrationJobResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var job = await service.GetByIdAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(ToResponse(job));
    }

    private static IntegrationJobResponse ToResponse(IntegrationJob job) => new(
        job.Id, job.Connector.ToString(), job.Operation, job.ExternalId, job.Status.ToString(),
        job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.UpdatedAtUtc);
}
