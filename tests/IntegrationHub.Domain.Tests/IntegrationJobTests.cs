using IntegrationHub.Domain;

namespace IntegrationHub.Domain.Tests;

public sealed class IntegrationJobTests
{
    private static IntegrationJob Create() => new(ConnectorType.Crm, "SyncCustomer", "customer-123");

    [Fact]
    public void Valid_creation_sets_identity_pending_state_and_utc_timestamp()
    {
        var before = DateTime.UtcNow;
        var job = Create();
        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.NotEqual(job.Id, Create().Id);
        Assert.Equal(ConnectorType.Crm, job.Connector);
        Assert.Equal("SyncCustomer", job.Operation);
        Assert.Equal("customer-123", job.ExternalId);
        Assert.Equal(IntegrationJobStatus.Pending, job.Status);
        Assert.Equal(DateTimeKind.Utc, job.CreatedAtUtc.Kind);
        Assert.InRange(job.CreatedAtUtc, before, DateTime.UtcNow);
        Assert.Null(job.StartedAtUtc);
        Assert.Null(job.CompletedAtUtc);
        Assert.Null(job.UpdatedAtUtc);
    }

    [Fact]
    public void Creation_trims_both_required_fields()
    {
        var job = new IntegrationJob(ConnectorType.Erp, "  Sync  ", "  external  ");
        Assert.Equal("Sync", job.Operation);
        Assert.Equal("external", job.ExternalId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_operation_is_rejected(string? value) =>
        Assert.Throws<ArgumentException>(() => new IntegrationJob(ConnectorType.Crm, value!, "id"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_external_id_is_rejected(string? value) =>
        Assert.Throws<ArgumentException>(() => new IntegrationJob(ConnectorType.Crm, "Sync", value!));

    [Theory]
    [InlineData(101, 1)]
    [InlineData(1, 201)]
    public void Oversized_fields_are_rejected(int operationLength, int externalLength) =>
        Assert.Throws<ArgumentException>(() => new IntegrationJob(ConnectorType.Crm,
            new string('a', operationLength), new string('b', externalLength)));

    [Theory]
    [InlineData(ConnectorType.Crm)]
    [InlineData(ConnectorType.Erp)]
    [InlineData(ConnectorType.Payments)]
    public void Defined_connectors_and_exact_limits_are_accepted(ConnectorType connector)
    {
        var job = new IntegrationJob(connector, new string('a', 100), new string('b', 200));
        Assert.Equal(connector, job.Connector);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Undefined_connector_is_rejected(int connector) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new IntegrationJob((ConnectorType)connector, "Sync", "id"));

    [Fact]
    public void Processing_sets_start_and_update_together()
    {
        var job = Create();
        var before = DateTime.UtcNow;
        job.MarkProcessing();
        Assert.Equal(IntegrationJobStatus.Processing, job.Status);
        Assert.NotNull(job.StartedAtUtc);
        Assert.Equal(DateTimeKind.Utc, job.StartedAtUtc.Value.Kind);
        Assert.InRange(job.StartedAtUtc.Value, before, DateTime.UtcNow);
        Assert.Equal(job.StartedAtUtc, job.UpdatedAtUtc);
        Assert.Null(job.CompletedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completion_sets_terminal_state_and_timestamps(bool succeeded)
    {
        var job = Create();
        job.MarkProcessing();
        var started = job.StartedAtUtc;
        var created = job.CreatedAtUtc;
        var before = DateTime.UtcNow;
        if (succeeded) job.MarkSucceeded(); else job.MarkFailed();
        Assert.Equal(succeeded ? IntegrationJobStatus.Succeeded : IntegrationJobStatus.Failed, job.Status);
        Assert.NotNull(job.CompletedAtUtc);
        Assert.Equal(DateTimeKind.Utc, job.CompletedAtUtc.Value.Kind);
        Assert.InRange(job.CompletedAtUtc.Value, before, DateTime.UtcNow);
        Assert.Equal(job.CompletedAtUtc, job.UpdatedAtUtc);
        Assert.Equal(started, job.StartedAtUtc);
        Assert.Equal(created, job.CreatedAtUtc);
    }

    [Theory]
    [InlineData(IntegrationJobStatus.Pending, "succeed")]
    [InlineData(IntegrationJobStatus.Pending, "fail")]
    [InlineData(IntegrationJobStatus.Processing, "process")]
    [InlineData(IntegrationJobStatus.Succeeded, "process")]
    [InlineData(IntegrationJobStatus.Succeeded, "succeed")]
    [InlineData(IntegrationJobStatus.Succeeded, "fail")]
    [InlineData(IntegrationJobStatus.Failed, "process")]
    [InlineData(IntegrationJobStatus.Failed, "succeed")]
    [InlineData(IntegrationJobStatus.Failed, "fail")]
    public void Invalid_transition_preserves_state_and_timestamps(IntegrationJobStatus status, string action)
    {
        var job = Create();
        if (status != IntegrationJobStatus.Pending) job.MarkProcessing();
        if (status == IntegrationJobStatus.Succeeded) job.MarkSucceeded();
        if (status == IntegrationJobStatus.Failed) job.MarkFailed();
        var timestamps = (job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.UpdatedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (action == "process") job.MarkProcessing();
            else if (action == "succeed") job.MarkSucceeded();
            else job.MarkFailed();
        });
        Assert.Equal(status, job.Status);
        Assert.Equal(timestamps, (job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.UpdatedAtUtc));
    }
}
