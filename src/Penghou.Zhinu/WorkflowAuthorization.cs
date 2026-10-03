using Penghou.Workflow.Abstractions;

namespace Penghou.Zhinu;

/// <summary>Immutable declarations for one protected operation, including its approved plan identity.</summary>
public sealed record WorkflowAuthorizationDeclaration
{
    public WorkflowAuthorizationDeclaration(
        IReadOnlyCollection<ExecutionRequirement> requirements,
        string? planId = null,
        string? planRevision = null)
    {
        var identity = new ExecutionIdentity("validation", "validation", 1, "validation",
            planId: planId, planRevision: planRevision);
        var context = new ExecutionAuthorizationContext(identity, "validation", requirements);
        Requirements = context.Requirements;
        PlanId = identity.PlanId;
        PlanRevision = identity.PlanRevision;
    }

    public IReadOnlyCollection<ExecutionRequirement> Requirements { get; }
    public string? PlanId { get; }
    public string? PlanRevision { get; }
}

/// <summary>Optionally verifies independently required provider evidence using trusted host services.</summary>
public interface IWorkflowAuthorizationEvidenceVerifier
{
    ValueTask<bool> VerifyAsync(ExecutionAuthorizationContext context,
        ExecutionAuthorizationResult result, CancellationToken cancellationToken = default);
}

/// <summary>One immutable authority configuration. BindingId identifies the host namespace and trusted mapping/profile version.</summary>
public sealed class WorkflowExecutionAuthorizationOptions
{
    public WorkflowExecutionAuthorizationOptions(string providerId, string bindingId,
        IExecutionAuthorizer authorizer,
        IWorkflowAuthorizationEvidenceVerifier? evidenceVerifier = null,
        TimeSpan? evaluationTimeout = null, TimeSpan? maximumDecisionLifetime = null,
        TimeSpan? maximumClockSkew = null)
    {
        var validated = new ExecutionIdentity(providerId, bindingId, 1, "validation");
        ProviderId = validated.ExecutionId;
        BindingId = validated.OperationId;
        Authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        EvidenceVerifier = evidenceVerifier;
        EvaluationTimeout = evaluationTimeout ?? TimeSpan.FromSeconds(30);
        MaximumDecisionLifetime = maximumDecisionLifetime ?? TimeSpan.FromMinutes(5);
        MaximumClockSkew = maximumClockSkew ?? TimeSpan.FromSeconds(5);
        if (EvaluationTimeout <= TimeSpan.Zero || EvaluationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(evaluationTimeout));
        if (MaximumDecisionLifetime <= TimeSpan.Zero || MaximumDecisionLifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(maximumDecisionLifetime));
        if (MaximumClockSkew < TimeSpan.Zero || MaximumClockSkew > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumClockSkew));
    }

    public string ProviderId { get; }
    public string BindingId { get; }
    public IExecutionAuthorizer Authorizer { get; }
    public IWorkflowAuthorizationEvidenceVerifier? EvidenceVerifier { get; }
    public TimeSpan EvaluationTimeout { get; }
    public TimeSpan MaximumDecisionLifetime { get; }
    public TimeSpan MaximumClockSkew { get; }
}

/// <summary>A fenced, immutable authorization outcome committed before dispatch or suspension.</summary>
public sealed record WorkflowAuthorizationCommit
{
    public required Guid WorkflowRunId { get; init; }
    public required Guid ClaimId { get; init; }
    public required string StepKey { get; init; }
    public required int Revision { get; init; }
    public required long LeaseGeneration { get; init; }
    public required string OwnerId { get; init; }
    public bool IsCompensation { get; init; }
    public required string BindingId { get; init; }
    public required string DeclarationHash { get; init; }
    public required string ContextHash { get; init; }
    public required ExecutionAuthorizationContext Context { get; init; }
    public required ExecutionAuthorizationResult Result { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>A durable pending approval. The snapshot is evidence, never a reusable permission.</summary>
public sealed record WorkflowAuthorizationPending
{
    public required Guid WorkflowRunId { get; init; }
    public required Guid ClaimId { get; init; }
    public required string StepKey { get; init; }
    public required int Revision { get; init; }
    public required long LeaseGeneration { get; init; }
    public bool IsCompensation { get; init; }
    public required string BindingId { get; init; }
    public required string DeclarationHash { get; init; }
    public required string ContextHash { get; init; }
    public required ExecutionAuthorizationContext Context { get; init; }
    public required ExecutionAuthorizationResult Result { get; init; }
    public bool Ready { get; init; }
}

/// <summary>Exact correlation submitted by authenticated host approval orchestration. A wake triggers reevaluation only.</summary>
public sealed record WorkflowAuthorizationWake
{
    public required Guid WorkflowRunId { get; init; }
    public required string AuthorizationRequestId { get; init; }
    public required string ApprovalRequestId { get; init; }
    public required string ProviderId { get; init; }
    public required string BindingId { get; init; }
    public required string ContextHash { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// Optional store capability required for protected execution. Commits atomically
/// validate current run/claim ownership, bind declarations, persist outcome evidence,
/// and either record dispatch start or park without consuming a callback attempt.
/// This is runtime fencing, not an atomic authorization transaction for external effects.
/// </summary>
public interface IWorkflowAuthorizationRepository
{
    ValueTask<bool> ValidateAuthorizationDispatchAsync(WorkflowAuthorizationCommit request,
        CancellationToken cancellationToken = default);
    ValueTask<bool> RenewAuthorizationClaimLeaseAsync(Guid workflowRunId, Guid claimId,
        bool isCompensation, string ownerId, long leaseGeneration, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    ValueTask<WorkflowAuthorizationPending?> GetPendingAuthorizationAsync(Guid workflowRunId,
        Guid claimId, bool isCompensation, CancellationToken cancellationToken = default);
    ValueTask CommitAuthorizationAsync(WorkflowAuthorizationCommit request,
        CancellationToken cancellationToken = default);
    ValueTask<bool> WakeAuthorizationAsync(WorkflowAuthorizationWake request,
        CancellationToken cancellationToken = default);
    ValueTask<bool> HasPendingAuthorizationsAsync(Guid workflowRunId,
        CancellationToken cancellationToken = default);
}

/// <summary>Terminal pre-dispatch authorization failure; it never consumes activity retry allowance.</summary>
public sealed class WorkflowAuthorizationException : Exception
{
    public WorkflowAuthorizationException(string message) : base(message) { }
}
