# Workflow authorization boundary

Zhinu consumes the published `Penghou.Workflow.Abstractions` contract and owns
its runtime checks and SQLite evidence. Applications supply an
`IExecutionAuthorizer` from a trusted policy implementation. The planned
`Penghou.Hufu.Workflow` adapter will implement that neutral contract; it has
not yet shipped. Zhinu has no Hufu dependency.

## Host configuration and declarations

Configure one immutable provider profile through direct options, the builder,
or hosting registration. For example, with an application-supplied authorizer:

```csharp
var authority = new WorkflowExecutionAuthorizationOptions(
    providerId: "my-policy",
    bindingId: "tenant-namespace-and-mapping-v1",
    authorizer: authorizer);

var engine = new WorkflowEngineBuilder()
    .WithStore(store)
    .WithRegistry(registry)
    .WithExecutionAuthorization(authority)
    .Build();
```

For hosted execution use `services.AddZhinuExecutionAuthorization(...)` alongside
`AddZhinu`. Either registration order is supported. Duplicate providers and
conflicting options registrations fail explicitly. A protected store must
implement `IWorkflowAuthorizationRepository`; SQLite implements it. Existing
custom stores remain usable without a provider.

Hosted registration takes the provider/profile identity and authorizer instance:

```csharp
services.AddZhinuExecutionAuthorization(
    providerId: "my-policy",
    bindingId: "tenant-namespace-and-mapping-v1",
    authorizer: authorizer);
```

Do not set `ExecutionAuthorization` inside the `AddZhinu` options callback;
that path is explicitly rejected. Direct constructors may use
`new ZhinuOptions { ExecutionAuthorization = authority }`; the builder uses
`WithExecutionAuthorization(authority)`.

Every protected durable activity evaluates its explicit declaration, including
an empty declaration if omitted. Empty declarations confer no permission; the
configured provider decides whether its vocabulary and policy recognize them.
Hufu's protected mapping is intended to reject empty or unsupported declarations.
Framework-generated durable helper steps can also present empty declarations.
An adapter with that default must define a reviewed, trusted mapping for those
helpers; an empty list or a caller-controlled step key alone must not imply Allow.
Provide forward and compensation requirements separately:

```csharp
var stepOptions = new StepOptions
{
    Authorization = new WorkflowAuthorizationDeclaration(
        [new ExecutionRequirement("my.capabilities", 1, "execute", "retained-operation")],
        planId: "retained-plan", planRevision: "exact-plan-revision"),
    CompensationAuthorization = new WorkflowAuthorizationDeclaration(
        [new ExecutionRequirement("my.capabilities", 1, "compensate", "retained-operation")],
        planId: "retained-plan", planRevision: "exact-plan-revision")
};
```

Child admission and result waits have separate declarations through
`ChildRunOptions.StartAuthorization` and `WaitAuthorization`. Each child also
gets the parent's effective provider profile and an independent run identity;
its activity callbacks require their own declarations. A parent relationship
does not authenticate a subject or increase its authority ceiling.

With no configured provider, the named **legacy unprotected profile** preserves
existing unenforced execution. It is never a fallback for provider failure.
Run admission retains the provider and a version-one hash of the trusted host
binding, evidence requirement and timing profile. Recovery, children, forks and
rollback cannot silently change that profile. The host must change `BindingId`
when its trusted principal/tenant mapping or policy interpretation changes;
workflow IDs, plan labels and diagnostic metadata do not authenticate actors.

## Neutral identity mapping

The engine derives these facts from the actual run and acquired claim:

| Neutral field | Zhinu mapping |
| --- | --- |
| `ExecutionId` | Run GUID in `N` format |
| `ParentExecutionId` | Parent run GUID in `N` format, when present |
| `OperationId` | `step:<claim-guid-N>` or `compensation:<claim-guid-N>` |
| `OperationPath` | Durable step key |
| `Attempt` | Current intended dispatch attempt |
| `ExecutionRevision` | `g<lease-generation>:r<step-revision>` |
| `PlanId`, `PlanRevision` | Declared retained plan identity; declarative activities use compiled name and version |
| `AuthorizationRequestId` | Fresh GUID in `N` format for each evaluation |
| `Requirements` | Immutable snapshot of the operation's declaration |

Declarative recovery also checks the separately retained definition fingerprint.
It is not placed in `PlanRevision`. Provider/binding identity and required
evidence belong to trusted host configuration. Correlation IDs and resource
labels are data; adapters must authenticate actors and interpret requirement
schemas explicitly.

Provider checks have a bounded timeout (30 seconds by default), a finite Allowed
lifetime limit (five minutes), and bounded future clock skew (five seconds).
Hosts should configure the same trusted `TimeProvider` for the engine and store.
Null, mismatched, expired, throwing and unavailable responses never dispatch.
If an evidence verifier is configured, it must validate the provider evidence
reference independently; a reference string alone proves nothing.

## Durable outcomes and approval

The runtime snapshots each declaration, creates a fresh request ID for every
evaluation, and binds the complete context to the effective host profile with
a version-one SHA-256 digest. JSON uses fixed runtime options and known property
order, with unknown members rejected on checkpoint decoding. SQLite bounds
serialized context evidence to 2 MiB and result evidence to 32 KiB; neutral value
count and UTF-8 bounds still apply before serialization.

Before dispatch, SQLite atomically validates current run/claim ownership,
generation, revision, intended attempt, declaration and binding, then persists
the outcome and dispatch-start marker. It samples its own current clock after
acquiring the transaction. A final validation rechecks the committed decision
and current fence. Required evidence failure blocks dispatch. Authorizer and
human approval waits take place outside writer transactions.

Denied, Error and Unavailable terminate the operation and run without executing
the callback or spending its retry allowance. Caller cancellation remains
cancellation. Authorization infrastructure has no automatic retry loop; an
operator can explicitly restart failed work. Actual callback failure uses the
existing durable retry policy, and every acquired retry obtains a fresh decision.
The attempt counter is based on persisted intended dispatch-start markers,
including interrupted starts. Allowed evidence and the marker commit before
final dispatch validation; the marker alone does not prove the callback entered
or that an external effect completed.

ApprovalRequired persists a separate pending checkpoint and releases worker
capacity and leases. The scheduler and direct execution do not repeatedly
evaluate a parked request. Trusted, authenticated approval orchestration reads
`GetPendingAuthorizationAsync` and submits `WorkflowAuthorizationWake` with the
exact run, request, approval, provider, effective binding and context digest.
`WakeAuthorizationAsync` changes readiness only. Duplicate accepted wakes are
idempotent and stale or mismatched wakes cannot wake a newer request. After wake
or recovery the runtime constructs a fresh request and rechecks current policy;
a revoked approval can still be denied. A swallowed denial or pending approval
cannot become durable success.

This is callback preflight, not native-code containment or an atomic transaction
with external effects. Concrete resource access, current revocation and mutation
start/outcome checks remain the resource provider's responsibility. Durable
authorization evidence is history, never a bearer token or completed-effect receipt.

## Declarative activities and loops

Declarative activity authorization is declared as a bounded immutable
`WorkflowAuthorizationDeclaration` on its `ActivityDescriptor`. The activity
catalogue snapshots the requirements at registration, and compilation takes a
second snapshot. The canonical compiled fingerprint includes every declared
requirement in order. A descriptor declaration supplies requirements only;
compiled execution binds the workflow name and version as its plan identity.
Callers cannot put an unrelated plan ID or revision on an activity descriptor.

When a protected declarative step is dispatched, its executor resolver runs
inside the common `StepAsync` callback. A denied or unavailable authorization
therefore prevents both resolver activation and executor invocation. A step
that returns a historical completed result does not resolve or invoke the
activity again.

The loop continuation predicate also runs inside a durable `StepAsync`
callback. Hosts may declare its requirements with
`LoopOptions.ContinueWhileAuthorization`; the runtime derives the plan and
operation identity from the retained declaration and loop condition step. A
loop declaration's optional plan ID and revision must describe the exact
workflow plan. The loop body is workflow
orchestration and runs outside activity preflight. Loop keys, state selection,
body control flow and arbitrary orchestration code are outside this boundary.
Each nested protected step needs its own declaration. External effects still
need resource-level authorization.

During rollback, previously completed forward steps are reconstructed from
history so compensation can be rebound. The loop body orchestration itself may
run again while that reconstruction happens; it is outside activity preflight.
A recorded authorization result is never a reusable permit for a newly
dispatched callback. Any predicate or nested activity that runs again is
subject to a fresh authorization decision.
