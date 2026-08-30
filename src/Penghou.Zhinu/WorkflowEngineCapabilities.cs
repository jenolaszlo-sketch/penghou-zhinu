namespace Penghou.Zhinu;

/// <summary>Capabilities used by execution hosts and schedulers.</summary>
public interface IWorkflowRuntime
{
    Task<Guid> StartAsync<TInput>(string workflowName, string workflowVersion,
        TInput input, Guid? workflowRunId = null, DateTimeOffset? deadline = null,
        object? metadata = null, CancellationToken cancellationToken = default);
    Task ExecuteAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    Task<int> RunAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>Capabilities used by applications interacting with workflow runs.</summary>
public interface IWorkflowClient
{
    Task<WorkflowRun?> GetRunAsync(Guid workflowRunId, CancellationToken cancellationToken = default);
    Task<TOutput> WaitForCompletionAsync<TOutput>(Guid workflowRunId,
        DateTimeOffset? deadline = null, CancellationToken cancellationToken = default);
    Task SendSignalAsync(Guid workflowRunId, string signalName, object? data = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional application capability for retry-safe external signals.</summary>
public interface IIdempotentWorkflowClient
{
    Task<SignalSendReceipt> SendSignalWithReceiptAsync(
        Guid workflowRunId,
        string signalName,
        SignalSendOptions options,
        object? data = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Capabilities that mutate or administer existing workflow runs.</summary>
public interface IWorkflowAdministration
{
    Task CancelAsync(Guid workflowRunId, string? actor, string? reason,
        CancellationToken cancellationToken = default);
}
