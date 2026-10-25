namespace Penghou.Zhinu;

/// <summary>Represents the current durable state of one workflow execution.</summary>
public sealed record WorkflowRun
{
    public required Guid Id { get; init; }

    public required string WorkflowName { get; init; }

    public required string WorkflowVersion { get; init; }

    public required WorkflowStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// The absolute time after which the run is no longer eligible to execute.
    /// When a run is claimed after its deadline passes, the engine fails it with
    /// a timeout error instead of executing it. Null means no deadline.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    public string? InputJson { get; init; }

    public string? InputType { get; init; }

    public string? OutputJson { get; init; }

    public string? OutputType { get; init; }

    public WorkflowError? Error { get; init; }

    /// <summary>
    /// The run that started this run as a child workflow, if any. Child runs are
    /// ordinary runs with their own steps and events; the parent durably waits
    /// for their completion.
    /// </summary>
    public Guid? ParentRunId { get; init; }

    /// <summary>
    /// Source run whose committed step results seeded this run. Fork lineage is
    /// independent of child-workflow ancestry in <see cref="ParentRunId"/>.
    /// </summary>
    public Guid? SourceRunId { get; init; }

    /// <summary>
    /// Optional caller-supplied metadata serialized as JSON (for example
    /// correlation ids, owners, or tags). Metadata does not participate in
    /// idempotency or workflow contract validation.
    /// </summary>
    public string? MetadataJson { get; init; }

    /// <summary>
    /// W3C trace identifier used to correlate separate execution segments when
    /// this durable run resumes in another process. It is diagnostic only.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Deterministic fingerprint of the workflow definition this run was started
    /// from, when the registered workflow carries one (for example a compiled
    /// declarative definition). Execution rejects a run whose registered
    /// definition fingerprint no longer matches this value, so a changed
    /// definition cannot silently resume an older run.
    /// </summary>
    public string? DefinitionFingerprint { get; init; }

    /// <summary>Optional immutable annotation pin, independent of executable identity and mutable metadata.</summary>
    public string? GroupManifestDigest { get; init; }

    /// <summary>Durable marker set by first grouped fan-out admission; requires its optional store capability on resume.</summary>
    public bool GroupFanOutUsed { get; init; }

    /// <summary>Configured authorization provider bound when this run was admitted.</summary>
    public string? AuthorizationProviderId { get; init; }

    /// <summary>Trusted host binding profile paired with <see cref="AuthorizationProviderId"/>.</summary>
    public string? AuthorizationBindingId { get; init; }

    /// <summary>
    /// Opaque durable authority-subject binding: identifies whose current
    /// authority governs this run's protected runtime effects. Owned
    /// entirely by the host contract (Zhinu never interprets it); set once
    /// when the run is admitted and never updated afterward. Null means the
    /// run carries no authority identity: requesting an authorized runtime
    /// effect for such a run must fail closed. This is identity only, never
    /// a permission grant and never a stored authorization decision.
    /// </summary>
    public string? AuthoritySubjectBinding { get; init; }

    public string? LeaseOwner { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// Monotonic fencing token for the current run lease. Every claim and every
    /// step restart increments it; step writes are only accepted while a step
    /// row's generation matches the run's current generation, which rejects
    /// commits from workers that held a lease before an administrative restart.
    /// </summary>
    public long LeaseGeneration { get; init; } = 1;
}
