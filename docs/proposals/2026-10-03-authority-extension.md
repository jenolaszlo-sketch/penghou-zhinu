# Zhinu Authority Extension Point and Hufu Integration

## Status

Proposed architecture change.

## Purpose

Invert the current Hufu and Zhinu relationship.

Hufu must not depend on the Zhinu runtime in order to provide authority, policy, delegation, revocation, or enforcement.

Instead:

- Zhinu defines a small authorization extension point in its abstractions.
- Zhinu invokes that extension point before executing protected workflow operations.
- Hufu optionally implements the extension point.
- Zhinu remains fully usable without Hufu.
- Hufu remains fully usable without Zhinu.
- Fine-grained side-effect enforcement remains in the relevant resource abstractions such as filesystem, process, HTTP, or future VFS implementations.

The desired dependency direction is:

```text
Penghou.Zhinu
        |
        v
Penghou.Zhinu.Abstractions

Penghou.Hufu.Zhinu
        |
        +----> Penghou.Hufu
        |
        +----> Penghou.Zhinu.Abstractions
```

Not:

```text
Penghou.Hufu -> Penghou.Zhinu
```

Hufu core must not depend on Zhinu.

---

# 1. Architectural Principle

Zhinu owns workflow execution.

Hufu owns authority.

Zhinu should know only that an execution may require authorization. It must not know:

- Hufu
- Cedar
- Biscuit
- grants
- revocation implementation
- approval stores
- authority envelopes
- policy languages
- token formats
- capability propagation rules

Zhinu asks:

> May this workflow operation execute?

Hufu may provide the answer, but Zhinu must not depend on Hufu to ask the question.

This follows dependency inversion: the workflow engine defines the contract it requires, and authority providers implement that contract.

---

# 2. Responsibilities

## 2.1 Zhinu

Zhinu owns:

- workflow execution
- activity lifecycle
- activity identity
- retries
- attempts
- suspension and resume
- checkpoints
- compensation
- workflow and plan identity
- execution metadata
- declared activity requirements

Zhinu must provide sufficient execution context for an authorization provider to make a decision.

Zhinu does not interpret Hufu policies.

## 2.2 Hufu

Hufu owns:

- grants
- restrictions
- revocation
- delegation
- authority inheritance
- approval state
- policy evaluation
- Cedar integration
- Biscuit integration
- capability envelopes
- authority provenance
- diagnostics
- "why denied?"
- "what permission would be sufficient?"
- plan-aware authority rules

Hufu may use Zhinu metadata as input to authorization, but Hufu core must not require Zhinu.

## 2.3 Resource abstractions

Resource abstractions own enforcement of actual effects.

Examples:

```text
Penghou.IO.Abstractions
Penghou.Process.Abstractions
Penghou.Http.Abstractions
future VFS abstractions
```

These enforce operations such as:

```text
read file
write file
create directory
delete file
execute process
open network connection
send HTTP request
```

Zhinu-level authorization does not replace resource-level authorization.

---

# 3. Two-Level Enforcement Model

The architecture must support two distinct levels of authorization.

## Level 1: Workflow execution authorization

Zhinu asks whether an activity or workflow operation is allowed to begin.

Example:

```text
Activity:
build-parser

Declared requirements:
- read ./src/**
- write ./artifacts/**
- execute dotnet
```

Before execution:

```text
Zhinu
  |
  v
IActivityExecutionAuthorizer
  |
  v
allow / deny / approval required
```

This validates declared intent.

## Level 2: Runtime effect authorization

During execution, actual operations are checked by controlled resource abstractions.

Example:

```text
activity
   |
   v
IFileSystem.ReadAsync("./src/parser.cs")
   |
   v
Hufu-backed filesystem decorator
   |
   v
authorization
```

This validates actual effects.

Both levels are required.

Preflight authorization cannot guarantee that an activity will only perform its declared effects.

Runtime enforcement cannot determine whether the overall workflow plan was approved.

They solve different problems.

---

# 4. Zhinu Abstraction

Add a narrow authorization extension point to `Penghou.Zhinu.Abstractions`.

Suggested name:

```csharp
IActivityExecutionAuthorizer
```

Alternative names are acceptable if they remain generic and contain no Hufu-specific terminology.

Example:

```csharp
public interface IActivityExecutionAuthorizer
{
    ValueTask<ActivityExecutionAuthorizationResult> AuthorizeAsync(
        ActivityExecutionAuthorizationContext context,
        CancellationToken cancellationToken = default);
}
```

The abstraction must describe a Zhinu requirement, not an external authorization implementation.

Do not introduce:

```csharp
IHufuAuthorizationService
ICedarAuthorizer
IBiscuitAuthorizer
IAuthorityGrantService
```

into Zhinu.

---

# 5. Authorization Context

Zhinu should expose enough information for an implementation to identify the execution and declared authority requirements.

Suggested model:

```csharp
public sealed record ActivityExecutionAuthorizationContext
{
    public required string WorkflowId { get; init; }

    public required string ExecutionId { get; init; }

    public required string ActivityId { get; init; }

    public string? ActivityPath { get; init; }

    public int Attempt { get; init; }

    public string? PlanId { get; init; }

    public string? PlanRevision { get; init; }

    public string? ParentExecutionId { get; init; }

    public IReadOnlyCollection<ExecutionRequirement> Requirements { get; init; }
        = Array.Empty<ExecutionRequirement>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

Exact names may follow existing Zhinu terminology.

Avoid leaking internal Zhinu implementation details unnecessarily.

The context should be serializable or representable using durable data because workflow execution may cross:

- retries
- suspension
- process restarts
- persistence boundaries

---

# 6. Execution Requirements

Zhinu should expose generic declared requirements.

For example:

```csharp
public sealed record ExecutionRequirement(
    string Capability,
    string? Resource = null,
    IReadOnlyDictionary<string, string>? Properties = null);
```

Example requirements:

```text
Capability: filesystem.read
Resource: ./src/**

Capability: filesystem.write
Resource: ./artifacts/**

Capability: process.execute
Resource: dotnet

Capability: http.request
Resource: https://api.example.com/**
```

Zhinu must not define Hufu's internal grant representation.

The integration adapter translates Zhinu requirements into Hufu authority requests.

---

# 7. Authorization Result

The result must support more than Boolean allow/deny because Hufu may require human approval.

Suggested model:

```csharp
public enum ActivityExecutionAuthorizationStatus
{
    Allowed,
    Denied,
    ApprovalRequired
}
```

```csharp
public sealed record ActivityExecutionAuthorizationResult
{
    public required ActivityExecutionAuthorizationStatus Status { get; init; }

    public string? Reason { get; init; }

    public string? DecisionId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

Zhinu should treat these outcomes differently.

### Allowed

Continue execution.

### Denied

Do not execute the activity.

The result should become part of the workflow execution record.

### ApprovalRequired

Suspend execution using Zhinu's normal durable suspension mechanism.

The activity must not start.

Execution may resume after the relevant authorization state changes.

---

# 8. Default Behavior

Zhinu must work without any authorization provider configured.

Provide a default implementation:

```csharp
AllowAllActivityExecutionAuthorizer
```

Conceptually:

```csharp
public sealed class AllowAllActivityExecutionAuthorizer
    : IActivityExecutionAuthorizer
{
    public ValueTask<ActivityExecutionAuthorizationResult> AuthorizeAsync(
        ActivityExecutionAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(
            ActivityExecutionAuthorizationResult.Allowed());
    }
}
```

This preserves current Zhinu behavior.

Adding the abstraction must not make Hufu mandatory.

---

# 9. Zhinu Execution Point

Authorization must happen immediately before the activity becomes eligible to execute side effects.

Conceptually:

```text
activity selected
      |
      v
construct execution authorization context
      |
      v
authorize
      |
      +---- allowed ----------> execute
      |
      +---- denied -----------> fail/reject according to policy
      |
      +---- approval required -> suspend
```

Authorization must occur after enough runtime information exists to identify the actual execution attempt.

It should not occur so early that important execution context is unavailable.

It must occur before activity user code executes.

---

# 10. Retries

Authorization must be evaluated for every execution attempt unless Zhinu can prove that an existing authorization decision remains valid.

Initial implementation should prefer correctness:

```text
retry -> authorize again
```

This matters because authority may have changed since the previous attempt.

Example:

```text
attempt 1
allowed

grant revoked

attempt 2
must be denied
```

A previous allow decision must not silently authorize later retries.

---

# 11. Suspension and Resume

Authorization must be checked after durable suspension before protected execution resumes.

Example:

```text
activity requires write access

Hufu -> ApprovalRequired

Zhinu suspends

human grants access

workflow resumes

Zhinu authorizes again

Hufu -> Allowed

activity executes
```

The previous `ApprovalRequired` result is not itself permission to continue.

Resumption triggers a fresh authorization decision.

---

# 12. Revocation

The architecture must support authority being revoked while a workflow exists.

Zhinu-level authorization catches revocation before the next activity or retry.

Resource-level enforcement catches revocation during actual side effects where practical.

Example:

```text
activity starts with:
  read /src
  write /output

write permission revoked

activity later attempts:
  write /output/result.json

resource authorization:
  denied
```

This is why Zhinu preflight and resource enforcement must remain separate.

---

# 13. Hufu Zhinu Adapter

Create or retain an optional adapter package:

```text
Penghou.Hufu.Zhinu
```

Dependencies:

```text
Penghou.Hufu
Penghou.Zhinu.Abstractions
```

It must not require the full Zhinu runtime unless a concrete technical need exists that cannot be expressed through abstractions.

Primary implementation:

```csharp
HufuActivityExecutionAuthorizer
    : IActivityExecutionAuthorizer
```

Its responsibility is translation.

Conceptually:

```text
ActivityExecutionAuthorizationContext
          |
          v
HufuExecutionRequest
          |
          v
Hufu authorization pipeline
          |
          v
Hufu decision
          |
          v
ActivityExecutionAuthorizationResult
```

The adapter may map:

```text
WorkflowId
ExecutionId
ActivityId
ActivityPath
PlanId
PlanRevision
Attempt
Requirements
```

into Hufu's existing authority model.

The adapter must not move Hufu policy semantics into Zhinu.

---

# 14. Hufu Core Independence

The following projects must not reference Zhinu packages:

```text
Penghou.Hufu
Penghou.Hufu.Abstractions
Penghou.Hufu.Cedar
Penghou.Hufu.Biscuit
```

Any Zhinu-specific code belongs in:

```text
Penghou.Hufu.Zhinu
```

or an equivalent integration project.

A build of Hufu core must succeed without:

```text
Penghou.Zhinu
Penghou.Zhinu.Abstractions
```

present in the dependency graph.

---

# 15. Resource-Level Enforcement

This design is complementary to the abstraction work around filesystem, process, HTTP, and future resource access.

Example:

```text
Penghou.IO.Abstractions
    IFileSystem

Penghou.Process.Abstractions
    IProcessRunner

Penghou.Http.Abstractions
    IHttpTransport
```

Hufu may supply wrappers such as:

```text
HufuAuthorizingFileSystem
HufuAuthorizingProcessRunner
HufuAuthorizingHttpTransport
```

These intercept actual resource operations.

Example:

```text
Zhinu
 |
 | preflight
 v
HufuActivityExecutionAuthorizer
 |
 v
activity
 |
 v
IFileSystem
 |
 v
HufuAuthorizingFileSystem
 |
 v
physical or virtual filesystem
```

---

# 16. Virtual Filesystem Compatibility

The design must not assume that filesystem operations target the host operating system.

A future VFS should be injectable behind the same abstractions.

Example:

```text
IFileSystem
   |
   +-- PhysicalFileSystem
   |
   +-- InMemoryFileSystem
   |
   +-- VirtualFileSystem
   |
   +-- RecordingFileSystem
```

Hufu enforcement may decorate any of these.

Example:

```text
HufuAuthorizingFileSystem
        |
        v
VirtualFileSystem
```

or:

```text
RecordingFileSystem
        |
        v
HufuAuthorizingFileSystem
        |
        v
VirtualFileSystem
```

depending on the scenario.

No Zhinu or Hufu API should assume physical filesystem paths beyond the semantics explicitly defined by the IO abstraction.

---

# 17. WhatIf Execution

This architecture should support future WhatIf execution.

Example flow:

```text
workflow
   |
   v
Zhinu
   |
   v
Hufu preflight authorization
   |
   v
activity
   |
   v
in-memory / virtual resource implementations
   |
   v
record observed effects
   |
   v
compare:
declared effects
vs
observed effects
vs
authority policy
```

This could detect cases such as:

```text
declared:
read ./src/**

observed:
read ./src/**
write ./src/generated.cs
```

The workflow may have passed preflight based on declared intent, while WhatIf discovers an undeclared write.

That information can later feed:

- Hufu diagnostics
- workflow validation
- approval requests
- policy generation
- plan correction
- Fuwen validation

This is roadmap functionality, not required for the initial inversion.

---

# 18. Registration

Zhinu should provide a simple registration mechanism.

Example:

```csharp
services.AddZhinu(options =>
{
    options.ExecutionAuthorizer = ...;
});
```

or standard DI:

```csharp
services.AddSingleton<
    IActivityExecutionAuthorizer,
    HufuActivityExecutionAuthorizer>();
```

Prefer normal dependency injection if consistent with the existing Zhinu architecture.

There must be exactly one well-defined effective authorizer unless a compositional model is explicitly introduced later.

Do not accidentally create ordering-dependent authorization chains.

---

# 19. Failure Handling

Authorization infrastructure failures must fail closed when an authorizer is configured.

Examples:

```text
Hufu unavailable
policy evaluation throws
Biscuit verification fails unexpectedly
Cedar evaluation cannot complete
revocation store unavailable
```

must not become:

```text
authorization skipped
activity executes
```

The result should be distinguishable from an explicit policy denial.

Possible categories:

```text
Denied
ApprovalRequired
AuthorizationUnavailable
AuthorizationError
```

Exact modelling may depend on existing Zhinu failure semantics.

The important rule is:

> A configured authority provider failing must never silently become Allow.

The built-in allow-all provider is different because it represents the explicit absence of authority enforcement.

---

# 20. Observability

Zhinu should record authorization outcomes as execution evidence.

At minimum:

```text
workflow ID
execution ID
activity ID
attempt
decision status
decision ID if available
timestamp
authorization provider
```

Do not persist sensitive token contents or cryptographic material unless explicitly required.

Hufu should be able to correlate its decision records with Zhinu execution records.

Prefer correlation IDs over duplicating complete policy state.

---

# 21. Determinism and Replay

Historical workflow replay must distinguish between:

1. replaying recorded authorization outcomes
2. re-evaluating current authority

These have different purposes.

### Historical replay

Used to understand what happened.

Use recorded decision evidence.

### New execution

Used to perform effects.

Re-evaluate current authority.

Never treat a historical allow decision as current authorization for new side effects.

---

# 22. Package Structure

Target package structure:

```text
Penghou.Zhinu.Abstractions
    workflow contracts
    execution authorization contracts

Penghou.Zhinu
    durable workflow runtime
    default allow-all authorizer

Penghou.Hufu
    authority core

Penghou.Hufu.Cedar
    Cedar integration

Penghou.Hufu.Biscuit
    Biscuit integration

Penghou.Hufu.Zhinu
    Zhinu authorization adapter

Penghou.IO.Abstractions
    resource contracts

future:
Penghou.Hufu.IO
    Hufu enforcement decorators
```

No circular references are permitted.

---

# 23. Dependency Rules

Enforce these dependency rules in architecture tests where practical.

Allowed:

```text
Zhinu -> Zhinu.Abstractions

Hufu.Zhinu -> Hufu
Hufu.Zhinu -> Zhinu.Abstractions

Hufu.Cedar -> Hufu abstractions/core
Hufu.Biscuit -> Hufu abstractions/core
```

Forbidden:

```text
Hufu -> Zhinu
Hufu -> Zhinu.Abstractions

Zhinu -> Hufu
Zhinu.Abstractions -> Hufu

Zhinu -> Cedar
Zhinu -> Biscuit
```

The only bridge between the two domains should be the adapter.

---

# 24. Migration From Current Implementation

Inspect the current Hufu solution for all Zhinu references.

Classify each use into:

```text
A. genuinely generic Hufu functionality
B. Zhinu integration functionality
C. test-only integration functionality
```

Move all category B code into:

```text
Penghou.Hufu.Zhinu
```

Move category C tests into an integration-test project that may reference both systems.

Remove Zhinu package references from Hufu core projects.

Add the execution authorization abstraction to Zhinu.

Update Zhinu to invoke it.

Change the existing Hufu/Zhinu integration to implement the new interface.

Do not preserve an existing dependency merely because tests currently rely on it.

The architectural boundary is the source of truth.

---

# 25. Testing Requirements

## Zhinu tests

Verify:

```text
no authorizer configured -> execution continues using allow-all default

Allowed -> activity executes

Denied -> activity does not execute

ApprovalRequired -> activity suspends

resume -> authorization evaluated again

retry -> authorization evaluated again

authorizer throws -> activity does not execute

authorization runs before user activity code
```

## Hufu adapter tests

Verify translation of:

```text
workflow identity
execution identity
activity identity
activity path
attempt
plan ID
plan revision
requirements
metadata
```

Verify mapping from Hufu decisions to Zhinu results.

## Revocation tests

Example:

```text
attempt 1 allowed
grant revoked
attempt 2 denied
```

## Suspension tests

Example:

```text
first decision -> approval required
grant added
workflow resumed
second decision -> allowed
activity executes
```

## Package isolation tests

Verify:

```text
Penghou.Hufu builds without Zhinu runtime package

Penghou.Hufu core package dependency graph contains no Zhinu package

Penghou.Zhinu builds without Hufu
```

## Multi-target testing

Continue existing supported framework matrix, currently:

```text
.NET 8
.NET 10
```

---

# 26. Public API Compatibility

Adding the authorization extension point should be additive where possible.

Existing Zhinu users should not have to configure authorization.

Existing behavior should remain equivalent to:

```text
AllowAllActivityExecutionAuthorizer
```

Any unavoidable public API changes must be documented explicitly.

Public API baselines should be updated only for intentional changes.

---

# 27. Non-Goals

This change does not require:

- moving Hufu policy logic into Zhinu
- implementing Cedar inside Zhinu
- implementing Biscuit inside Zhinu
- making Zhinu responsible for filesystem enforcement
- implementing the VFS
- implementing full WhatIf execution
- implementing OS sandboxing
- predicting all runtime effects during preflight
- removing lazy runtime checks
- solving arbitrary native-process containment

Those remain separate concerns.

---

# 28. Security Invariants

The implementation must preserve these invariants.

### Invariant 1

Hufu core does not require Zhinu.

### Invariant 2

Zhinu does not require Hufu.

### Invariant 3

When authorization is configured, user activity code cannot execute before authorization succeeds.

### Invariant 4

A previous successful authorization does not automatically authorize a retry.

### Invariant 5

Resuming a suspended workflow does not bypass current authorization.

### Invariant 6

Workflow-level authorization does not replace resource-level authorization.

### Invariant 7

Authorization infrastructure failure does not silently become Allow.

### Invariant 8

Zhinu remains unaware of Hufu-specific policy technologies.

---

# 29. Acceptance Criteria

The work is complete when:

1. `Penghou.Hufu` has no dependency on Zhinu packages.
2. `Penghou.Zhinu` has no dependency on Hufu packages.
3. Zhinu exposes a generic execution authorization abstraction.
4. Zhinu calls the abstraction before activity execution.
5. Zhinu provides default allow-all behavior.
6. Hufu implements the abstraction through an optional adapter package.
7. Denial prevents activity execution.
8. Approval-required can suspend execution.
9. Authorization is repeated after resume.
10. Authorization is repeated for retries.
11. Hufu/Zhinu integration tests pass using published package dependencies rather than sibling source assumptions where appropriate.
12. Existing Zhinu behavior remains unchanged when no authority provider is installed.
13. Existing Hufu functionality works without Zhinu installed.
14. Dependency architecture tests prevent the coupling from being reintroduced.
15. .NET 8 and .NET 10 test suites pass.

---

# 30. Final Architecture

The intended architecture is:

```text
                    HUFU
              authority system
               /           \
              /             \
             v               v
Zhinu authorization      resource authorization
     adapter                 adapters
        |                       |
        v                       v
IActivityExecution       IFileSystem
Authorizer               IProcessRunner
        |                 IHttpTransport
        |                       |
        v                       v
      ZHINU                actual effects
workflow execution
```

Zhinu provides execution context.

Resource abstractions provide effect boundaries.

Hufu can enforce policy at both points without either subsystem being structurally dependent on Hufu itself.

The core principle is:

> Zhinu declares where authorization can participate. Hufu plugs into those boundaries. Hufu must not become part of Zhinu's execution model, and Zhinu must not become part of Hufu's authority model.