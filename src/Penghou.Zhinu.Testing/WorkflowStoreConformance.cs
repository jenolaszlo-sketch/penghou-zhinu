namespace Penghou.Zhinu.Testing;

/// <summary>Reusable smoke checks for custom durable-store implementations.</summary>
public static class WorkflowStoreConformance
{
    public static async Task VerifyRunRoundTripAsync(
        IWorkflowStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowName = "conformance",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            InputJson = "null",
            InputType = typeof(object).FullName,
            OutputType = typeof(object).FullName
        };
        await store.CreateRunAsync(run, cancellationToken).ConfigureAwait(false);
        var persisted = await store.GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (persisted is null || persisted.WorkflowName != run.WorkflowName)
            throw new InvalidOperationException("The workflow store failed the run round-trip contract.");
    }
}
