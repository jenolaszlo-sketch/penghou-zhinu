# Penghou.Zhinu Roadmap

## Vision

Penghou.Zhinu is an embedded durable workflow runtime for .NET and will evolve
into an execution substrate for compiled, enforceable AI workflows.

The central principle is:

> The workflow owns the process. Models perform bounded cognitive work inside
> it.

Prompts express intent. A compiler may translate that intent into a workflow
artifact, but Zhinu validates the artifact and the runtime enforces its legal
transitions, capabilities, evidence requirements, retries, and recovery.

The target lifecycle is:

```text
Intent → Compile → Validate → Approve → Execute → Enforce → Audit
```

Zhinu must remain independently useful as a code-first durable workflow engine.
AI authoring, model providers, and product-specific harnesses belong in adjacent
packages and applications.

## Product boundaries

### Zhinu owns

- Workflow artifact and intermediate-representation contracts
- Activity registration and discovery
- Structural, type, policy, and capability validation
- Durable execution and state transitions
- Capability enforcement at activity boundaries
- Evidence, provenance, and artifact-revision tracking
- Retries, timeouts, leases, fencing, compensation, and recovery
- Events, progress, tracing, metrics, and audit export

### Adjacent packages own

- Natural-language methodology compilation
- Model-provider integrations
- Tolerant repair of malformed model output
- Coding-agent tools and workspace adapters
- Product-specific user interfaces and approval experiences

Conceptually:

```text
Methodology compiler
        ↓
Validated WorkflowArtifact
        ↓
Penghou.Zhinu runtime
        ↓
Activities / bounded model calls
        ↓
Product harness such as Solo
```

## Design principles

1. **Enforcement below prompts.** Tool and resource restrictions must be
   enforced by the executor, not merely described to the model.
2. **Least-dynamic primitive.** Prefer native activities, then constrained
   expressions, then bounded AI activities, with open-ended agents as an escape
   hatch.
3. **Typed durable artifacts.** Stages exchange validated state rather than
   depending on complete conversation history.
4. **Deterministic control, explicit judgment.** A quality judgment may be
   probabilistic; the requirement to obtain it and the effect of failure are
   deterministic.
5. **Evidence is revision-bound.** Build, test, review, and approval evidence is
   valid only for the artifact or workspace revision it evaluated.
6. **Honest trust levels.** Declared side-effect behavior is not described as
   enforced unless the execution boundary can actually enforce it.
7. **Inspectable operation.** Every decision, transition, capability grant, and
   evidence record should be explainable after execution.
8. **No silent policy weakening.** Compilers and repair loops may correct syntax
   but may not remove or relax requirements merely to produce a valid artifact.

## Terminology

- **Validated:** accepted by structural, type, and semantic checks.
- **Enforced:** illegal transitions or capabilities are prevented at runtime.
- **Attested:** an activity produced durable evidence of an action or result.
- **Verified:** reserved for a precisely stated property that Zhinu can prove.

## Phase 0 — Durable runtime foundation

Status: substantially complete; continue hardening.

Existing foundations include durable steps, retries, timeouts, signals, child
workflows, dependencies, selective restart, previewable restart-as-new forks,
compensation, rollback, fencing, crash recovery, typed handles, diagnostics,
SQLite persistence, and durable external-artifact references with producing
step revision provenance and post-failure inspection. Operational hardening now
also includes artifact queries and validation, unified run snapshots,
deterministic run diagnosis, explicit SQLite schema compatibility checks, and
keyed class-based workflow steps with fresh scoped resolution per execution or
compensation attempt. Typed step references now bind implementation identity to
input/output contracts across registration and invocation; class-based fan-out
provides independently durable parallel items; forward step implementations can
emit events that commit atomically with their results.
Administrative step restart now accepts a stable operation identity and returns
an authoritative receipt. SQLite atomically commits the receipt with the
restart event and state transition; identical concurrent or post-crash retries
return the original receipt, while conflicting reuse is rejected. This behavior
is part of the reusable provider conformance suite.
Idempotent external signal send is also complete: callers may supply a stable
signal identity and receive an authoritative receipt. SQLite commits the inbox
row, `signal-sent` event, and receipt atomically; identical concurrent,
post-reopen, and post-purge retries reuse that receipt, while different run,
name, or canonical JSON payload reuse is rejected. Additive `SendSignalAsync`
semantics remain available when each call intentionally represents a new
signal, and optional-provider conformance covers receipt and conflict behavior.

Remaining foundation work:

- Complete a compatibility-API retirement pass before the package release after
  `0.1.0-preview.11`.
  Inventory every public member retained only for preview compatibility, mark it
  `[Obsolete]` with a concrete replacement and diagnostic ID, migrate Zhinu's
  own callers and tests, and list the planned removal version in the release
  notes. The initial review must cover `StepRestartMode.CreationOrder`, the
  non-receipt restart overloads, and the legacy
  `IWorkflowStepRepository.RestartStepAsync` provider contract. Do not deprecate
  deliberately supported non-idempotent behavior merely because it predates
  receipts; classify that behavior explicitly. Since current consumers are
  controlled, use the next development cycle to apply the obsolete markers,
  migrate those consumers, and remove confirmed compatibility-only APIs before
  cutting `0.1.0-preview.12`.
  Progress: `StepRestartMode.CreationOrder` now carries diagnostic
  `ZHINUOBS001`, documents its replacements and removal window, and retains
  narrowly suppressed compatibility tests. The restart overload and provider
  contract review remains open.
- Expand store conformance tests beyond round-trip smoke checks.
- Add stress tests for claims, leases, cancellation, and process-loss windows.
- Publish benchmark methodology and baseline results.
- Stabilize the preview API and document all transition guarantees.
- Improve administrative inspection of stuck runs and active operations.
- Complete the typed administrative failure taxonomy so restart, signal,
  cancellation, fork, rollback, and maintenance callers can distinguish
  not-found, invalid-state, definition-unavailable, stale generation or lease,
  idempotency conflict, timeout, cancellation, and provider failure without
  parsing exception messages or treating every failure as an ambiguous commit.

### Runtime and API hardening checkpoint

Before stabilizing the preview API, address the following review findings in
coherent batches rather than as isolated access-modifier changes:

- Split the consumer-facing `WorkflowEngine` surface behind narrow runtime,
  client/query, and administration contracts. `WorkflowEngine` may remain the
  implementation, but applications, hosting, and adapters should depend on the
  smallest relevant capability. Wire or remove the currently unused runtime
  abstraction as part of this work.
- Move definition/fingerprint validation behind an optional core definition-
  identity validation seam so the generic execution pipeline does not acquire
  declarative-specific responsibilities.
- Resolve registration identity without invoking a workflow factory merely to
  inspect its fingerprint; registration metadata must not construct
  heavyweight or side-effecting workflow instances unexpectedly.
- Define multi-engine behavior over one store. Make lease-recovery throttling
  concurrency-safe and document which coordination guarantees come from
  leases, fencing, the store, and in-process scheduling.
- Add a bounded graceful-shutdown policy instead of allowing engine disposal to
  wait for the full lease duration. Preserve fencing even when shutdown stops
  waiting for an activity.
- Cancel activity execution proactively when lease renewal reports ownership
  loss, while retaining the final fenced-write check as the durable authority.
- Add jittered/decorrelated backoff for hosted scans and lease renewal after
  transient failures to avoid synchronized retry storms across workers.
- Unify completion waiting with the existing event-channel wake-up path and
  centralize output contract validation/deserialization used by wait and result
  APIs.
- Remove the exact-page extra query from run enumeration and cancel or reuse
  losing subscription poll timers so idle subscribers do not create needless
  store work or short-lived timer churn.
- Complete the remaining public exception taxonomy. Typed workflow timeout and
  duplicate-registration/configuration failures are implemented; normal
  workflow operations must not leak other ambiguous raw BCL exceptions.
- Bring `ZhinuTestHost` cleanup, clock injection, and store configurability to
  parity with the hardened test fixtures, including Windows file-lock retries.
- Add an optional deep health probe for operational checks that must detect
  post-initialization schema damage; keep the default probe inexpensive.
- Record and publish the existing benchmark suite baseline on a representative
  machine, then use it to guard material regressions.

Recommended order:

1. shutdown timeout and typed public failures;
2. engine capability interfaces and definition-validation seam;
3. lease-loss cancellation, concurrency-safe recovery, and retry backoff;
4. test-host parity and benchmark baseline;
5. deep operational inspection only where deployment evidence requires it.

Exit criteria:

- Store and runtime invariants have executable conformance coverage.
- Recovery tests cover every durable operation phase.
- The runtime is dependable without any AI-specific packages.

### Durable state-loop checkpoint

Status: **executable, recovery-hardened, and operationally inspectable**.

Implemented in the first slice:

- `WorkflowContext.LoopAsync<TState>` with an explicit positive maximum,
  optional absolute deadline and durable relative wall-clock budget, and a
  deterministic precondition contract;
- one-based runtime iteration identity exposed through a typed iteration
  context, iteration-scoped body steps, and an iteration-local dependency
  helper;
- durable condition decisions that bind typed state and the configured maximum;
- fenced state commits built on the existing atomic step/event transaction;
- dependency chains that preserve earlier iterations and invalidate the
  selected/later iterations, final result, and downstream work;
- replay-safe iteration-committed, loop-completed, and limit-exceeded evidence;
- scoped typed `Continue(nextState)` and `Break(finalState)` outcomes persisted
  with the iteration commit, including replay that does not re-enter a body
  after its disposition has committed;
- cross-scope outcome rejection and early-break restart/replay coverage;
- rollback replay that deliberately re-enters loop bodies to rebind their
  compensation delegates while reusing all committed forward results;
- deterministic worker-interruption coverage before a body-step commit, before
  and after a continue commit, and after a break commit, proving one logical
  iteration commit and no body re-entry after a committed disposition;
- explicit cancellation coverage distinguishing resumable execution-token
  interruption from durable `CancelAsync`, including immediate lease release,
  committed-iteration reuse, cancellation-resistant delegate fencing,
  idempotent evidence, and parent/child terminal propagation;
- stale-generation coverage proving a replaced worker cannot commit an
  iteration or its event, followed by successful recovery under the new
  generation;
- condition-failure restart, transient body retry, terminal body-failure
  restart, and interrupted-compensation recovery with a stable idempotency key;
- typed internal root, nested-scope, and iteration identities with deterministic
  storage-key derivation and a 4,096-character encoded-key guard;
- lexical nested loops through `WorkflowLoopIteration.LoopAsync`, including
  parent-iteration ownership, collision isolation, transitive restart behavior,
  non-local control rejection, replay, and `MaxLoopNestingDepth` enforcement;
- public value-stable `WorkflowLoopReference`, iteration, and semantic boundary
  references for root and nested loops; grouped progress snapshots plus typed
  restart preview, restart, and idempotent receipt APIs on both the engine and
  typed run handle, without requiring callers to construct encoded step keys;
- typed `LoopLimitExceededException`, multi-target SQLite integration tests,
  and public API baseline coverage.
- a persisted resolved-limits boundary that chooses the earlier deadline or
  time budget once, survives worker interruption without renewal, fences state
  commits after expiry, emits typed replay-safe evidence, and is exposed through
  loop progress and administration references;

This slice deliberately adds no loop table or provider interface. Existing
step revisions, dependencies, events, and fenced completion already form the
provider-neutral persistence primitive and keep SQLite replaceable. Introduce
new store contracts only if the remaining crash/concurrency tests demonstrate
an invariant that cannot be expressed atomically through those primitives.

Still required before the checkpoint is complete:

- an OS-level subprocess termination test immediately before and after the
  state-commit boundary to corroborate the deterministic interruption suite;
- provider-conformance coverage for the composed loop behavior.

Implement durable state-dependent repetition as a first-class Zhinu runtime
construct before a public authoring DSL depends on it. Ordinary C# `for`,
`foreach`, and `while` statements are not durable loop declarations: they do
not expose a committed iteration boundary, loop-carried state, or an
unambiguous runtime iteration identity.

Keep two execution models distinct:

- `LoopAsync` represents sequential repetition where iteration `n + 1`
  consumes committed state produced by iteration `n`.
- `FanOutAsync` remains authoritative for independent keyed collection items,
  parallel sibling execution, per-item restart, and deterministic aggregation.
  The two constructs may share typed-key and identity utilities without being
  one persistence primitive.

#### Explicit loop-control outcomes

Evolve the body contract from an implicit next-state return to a closed,
typed `LoopBodyOutcome<TState>` with two normal outcomes:

- `Continue(nextState)` commits the current iteration's next immutable state
  and requests evaluation of the precondition for the following iteration.
- `Break(finalState)` commits the supplied final state, completes the current
  iteration, and then completes the loop normally without another condition
  evaluation or body execution.

`Continue` is the ordinary successful outcome, not equivalent to the C#
`continue` statement. An early return of `Continue` may skip later body code,
but already completed durable body steps remain part of the iteration and are
reused on replay. `Break` is not failure, cancellation, workflow return, or
process termination. It is a durable transition scoped only to the current
loop.

Do not add `Failure` as a normal union case. Exceptions, cancellation, retry,
and durable workflow failure remain authoritative so callers cannot accidentally
turn failed work into a successful loop-control result. The iteration context
should construct normal outcomes through APIs such as `Continue(nextState)` and
`Break(finalState)`; callers must not mutate hidden loop state or depend on
captured mutable variables.

Both outcomes must preserve the existing commit invariant. Their chosen state,
iteration disposition, and evidence must be durably bound at a fenced commit
boundary. A crash after that boundary must reuse the outcome rather than invoke
the body again. Completion may remain a following idempotent durable step if
failure-window tests prove that the composed transition is unambiguous and
recoverable; a new provider transaction is required only if composition cannot
provide that guarantee.

#### Nested loops and lexical scope

Nested loops use structurally scoped typed internal identity. Each nested
runtime instance includes:

- structural node path;
- loop instance;
- parent loop scope and parent iteration;
- its own one-based iteration number;
- body node path and revision.

Nested loops are created through the owning iteration's `LoopAsync` method,
not by calling unscoped `WorkflowContext.LoopAsync` from inside a body. Stable
serialized keys are derived internally from the typed scope and parent
iteration. Public query, restart, and adapter APIs should ultimately operate on
typed identities rather than make callers construct those encoded keys.

Loop control is lexical. `Break` or `Continue` applies only to the loop context
that created the outcome. An inner loop cannot directly break or continue an
outer loop. The inner loop must complete and return a value; the outer body can
then explicitly choose its own `Break` or `Continue` outcome. Initially, do not
support labels, non-local control, or passing an outer loop-control outcome from
inside an inner body.

Every loop body is a closed structured region. Generic graph references may
not jump into a body, jump out of it, cross between nested scopes, or emulate
loop control with arbitrary edges.

Fuwen must lower `continue with { ... }` and `break with { ... }` to these same
observable Zhinu outcomes. Unqualified DSL control targets the innermost
lexical loop. Fuwen should initially reject labeled or non-local break and
continue constructs and preserve structured loop regions in `WorkflowPlan`.

The initial state-loop contract must define:

- stable structural loop identity distinct from typed runtime iteration
  identity and body-step identity;
- a typed initial state, immutable canonical state revisions, state schema and
  content hash, and artifact references rather than embedded large payloads;
- precondition evaluation over committed state, with a future explicitly named
  postcondition construct rather than ambiguous condition timing;
- declared body inputs, state transition, final result, and closed region
  boundaries that forbid jumps into or out of internal body steps;
- at least one host-enforceable positive iteration bound, deadline, budget, or
  explicitly permitted durable-suspension rule, with host policy allowed to
  tighten but never expand source limits;
- distinct typed outcomes for false-condition completion, limit exhaustion,
  explicit body break, timeout, budget exhaustion, condition failure, body
  failure, and cancellation;
- stable downstream operation keys containing the loop instance, iteration,
  body step, and revision so retries do not duplicate external effects;
- iteration diagnostics exposing current iteration, committed state revision,
  configured limits, termination reason, and pending body position.

The iteration boundary is committed only when required body steps have
completed and the next immutable state revision is ready. Advancing the loop
cursor, committing that state revision, marking the iteration complete, and
emitting its event must occur in one fenced store transaction. A stale worker
must not advance the loop after lease loss. External effects retain the normal
effectively-once/at-least-once boundary and use the stable operation key for
downstream deduplication.

Recovery and restart semantics must guarantee:

- process loss before the iteration commit resumes the same logical iteration
  and reuses its already committed body steps;
- process loss after the commit observes the new state and never increments
  the logical iteration twice;
- restarting iteration `n` preserves valid earlier iterations and invalidates
  iteration `n`, every later iteration, the loop result, and downstream work;
- restarting a body step invalidates that iteration's state transition and all
  later derived work;
- changing the loop condition, bounds, state contract, body structure, or
  result binding changes definition identity and follows explicit
  compatibility rules.

Control-outcome and nesting tests must additionally prove:

- `Continue` commits exactly one next-state revision before condition
  evaluation resumes;
- `Break` commits its final state, completes normally, and never evaluates a
  later condition or body;
- replay after either outcome reuses the committed disposition;
- restarting the producing body step invalidates its outcome, current
  iteration commit, loop completion, and later derived work;
- an inner break affects only its inner loop, while outer termination requires
  an explicit decision by the outer body;
- identities for the same nested structural loop in different outer
  iterations cannot collide.

Code-first delegates cannot be assumed to be portable or fingerprintable.
Zhinu therefore requires stable names and explicit durable values while the
host remains responsible for code-version compatibility. Fuwen supplies a
fully compiled, fingerprinted declaration through its adapter later.

Acceptance requires provider conformance and process-loss tests before the
body, after each body step, while evaluating the condition, immediately before
the fenced iteration commit, and immediately after it. Tests must prove that a
resumed run neither skips nor duplicates a committed logical iteration.

## Phase 1 — Declarative workflow artifacts

Build a bounded, versioned workflow intermediate representation that can be
authored without generating arbitrary C#.

Initial grammar:

- Sequence
- Conditional branch
- Bounded loop
- Parallel fan-out and join
- Activity invocation
- Human approval
- Quality gate
- Terminal success and failure

Core types should include:

```text
WorkflowArtifact
WorkflowStateDefinition
WorkflowTransition
ActivityReference
InputBinding / OutputBinding
PolicyRequirement
CapabilityRequirement
EvidenceRequirement
```

Deliverables:

- Canonical JSON representation and JSON Schema
- Stable artifact identity, version, and content hash
- Artifact serializer with deterministic output
- Portable activity contract identities that do not serialize runtime-specific
  `System.Type` values into compiled artifacts
- Interpreter backed by the existing durable runtime
- A coherent public authoring surface covering definition construction,
  catalogue registration, compilation, registration, and execution
- Human-authored examples covering coding and non-AI workflows
- Artifact compatibility and versioning policy
- An authored-JSON loading path that deserializes, validates, and returns
  structured diagnostics, plus a published JSON Schema
- A one-shot compile-and-register path that centralizes canonicalization,
  fingerprint verification, contract validation, and registry mutation
- Declarative run inspection that reports definition fingerprint,
  step-to-activity mapping, status, results, and produced artifact references
- First-class declarative result access to durable artifact references without
  copying external artifact payloads

### Declarative correctness and usability checkpoint

Before expanding beyond the current linear vertical:

- Make the compiled definition's validated shape authoritative. The runtime
  must either iterate the validated linear chain directly or reject a
  hand-constructed compiled definition that violates the supported topology;
  it must not silently execute a graph the compiler would reject.
- Add compiler-bypass, fingerprint-tamper, restart-identity, and source-versus-
  compiled canonical-stability tests.
- Replace duplicate canonicalization options and algorithms with one frozen
  JSON configuration and shared canonicalizer core.
- Carry the compiler-produced fingerprint through registration while retaining
  an explicit defense-in-depth verification point; avoid accidental divergent
  source and compiled identity rules.
- Derive validation success from diagnostics or compiled output rather than
  maintaining adjacent hand-set and derived `IsValid` truths.
- Reuse precomputed step, descriptor, and executor maps during validation; do
  not repeatedly scan steps or resolve the same activity.
- Remove implicit object-cast dispatch from the activity execution boundary.
  Dispatch should use the compiled input/output contract and produce a typed,
  diagnosable mismatch rather than an unchecked cast or incidental JSON
  round-trip.
- Add ergonomic `ActivityReference.Parse`/`TryParse`, catalogue registration by
  name and version, JSON load/validate, and compile-and-register entry points.
  Keep the lower-level APIs available for tooling.
- Standardize declarative model construction conventions, including choosing
  one clear `ActivityReference` creation idiom instead of combining required
  initializers with a required-member-satisfying constructor.

Exit criteria:

- A hand-authored artifact can execute durably across process restarts.
- Invalid activity references, bindings, transitions, and cycles are rejected
  before execution.
- The same immutable artifact produces the same graph and policy model.
- A package consumer can compile and execute the documented example from a
  separate assembly using only public APIs.
- A compiled artifact round-trips through canonical JSON without requiring the
  originating process's CLR type objects.

### Bounded-loop design checkpoint

Loops remain outside the current linear declarative vertical. Declarative and
Fuwen adapters must consume the durable state-loop contract defined in Phase 0
rather than implement repetition through generic graph backedges. Before the
declarative surface is exposed, additionally define and test:

- canonical serialization of structured loop declarations and closed body
  regions without serializer `$id`/`$ref` cycles or backward control edges;
- mapping of structural Fuwen identities to typed Zhinu loop, iteration, and
  body-step identities;
- fingerprint and compatibility behavior when loop bounds or conditions change;
- diagnostics exposing current iteration, configured limit, termination reason,
  and exhausted-loop failures;
- validation rejecting unbounded loops and conditions with undeclared or
  non-durable state dependencies.

Acceptance requires process-loss tests at every loop boundary and proof that a
resumed run neither skips nor duplicates a committed logical iteration.

## Phase 2 — Activity catalogue and type system

Create a machine-readable catalogue from registered activities.

Each activity descriptor should include:

```text
name and version
description
input and output schema
required and produced capabilities
side-effect declaration
enforcement and trust level
idempotency and retry semantics
compensation support
execution environment
tags
```

Activity levels:

1. Native .NET activity
2. Constrained expression or script activity
3. Bounded AI activity
4. Open-ended agent activity

Deliverables:

- Typed activity interfaces and registration APIs
- One coherent public catalogue abstraction used by compiler and registration
  entry points. Either promote `IActivityCatalogue` as the supported extension
  contract or remove it; do not expose a concrete-only API alongside a private
  interface seam.
- Catalogue export for compilers and user interfaces
- Schema compatibility validation for bindings
- Activity version resolution rules
- Startup diagnostics for duplicate or incomplete registrations
- A DI-friendly registration and discovery path where demonstrated by hosting
  use, without requiring reflection-based global discovery
- Descriptor evolution points for idempotency, retry, compensation,
  capabilities, trust, and execution environment without weakening typed
  dispatch

Exit criteria:

- Artifacts can reference only registered activity versions.
- Every transition is type-compatible or has an explicit transformation.
- Runtime capabilities derive from the selected descriptor, not model output.

## Phase 3 — Policy validator and diagnostics

Implement deterministic policies over graphs, capabilities, types, evidence,
and provenance.

Initial policy families:

- Mandatory-state dominance of completion
- Forbidden transitions
- Capability reachability and separation
- Required approval before protected capabilities
- Required build/test/review evidence
- Failure preventing successful completion
- Bounded-loop and termination requirements
- Side-effect idempotency or compensation requirements
- Evidence freshness and invalidation

Diagnostics should provide:

- Stable code and severity
- Artifact source location
- Violated policy
- Counterexample path where applicable
- Suggested correction

Example:

```text
ZHINU-POLICY-002
Completion can bypass mandatory Test evidence.

Counterexample:
Start → Analyze → Implement → Review → Complete
```

Deliverables:

- Extensible policy-rule API
- Built-in deterministic rules
- Counterexample path generation
- Warning/error configuration without allowing hard laws to be downgraded
- Machine-readable diagnostics for compiler repair loops

Exit criteria:

- Every hard rule has positive, negative, and counterexample tests.
- A workflow with validation errors cannot begin execution.
- Compiler diagnostics are stable enough for automated correction loops.

## Phase 4 — Capability enforcement and evidence

Turn activity metadata into real execution constraints.

Capability examples:

```text
repository.read
repository.write
web.search
process.build
process.test
network.external
approval.issue
deployment.production
```

Deliverables:

- Unforgeable runtime capability grants
- Executor interfaces that expose only granted operations
- Resource scopes such as repository roots, domains, and command families
- Durable evidence records with producer, timestamp, inputs, outputs, and hashes
- Trust levels: enforced, sandboxed, developer-declared, external-attested, and
  probabilistic
- Evidence invalidation when dependent artifacts or workspace revisions change
- Audit export suitable for humans and automated evaluation

Exit criteria:

- A state lacking `repository.write` cannot mutate through any provided Zhinu
  executor.
- AI activities cannot manufacture human or privileged approval evidence.
- Completion gates reject stale build, test, review, or approval evidence.
- Capability grants and evidence provenance are visible in the event history.

## Phase 5 — Bounded AI activities

Add provider-neutral model invocation without giving models orchestration
ownership.

Proposed contract:

```csharp
IBoundedModelActivity<TInput, TOutput>
```

An invocation should fix:

- Objective
- Typed input and output schemas
- Allowed tools and resources
- Context-selection policy
- Token, time, and cost budgets
- Retry and structured-output repair policy
- Provider and model selection constraints
- Quality gate or evaluator

Deliverables:

- Provider-neutral request and result contracts
- Structured output validation
- Model-call provenance and usage records
- Bounded repair for malformed output
- AI and human evaluator activities
- Redaction and context-policy hooks
- Explicit handling of nondeterminism during retry and restart

Exit criteria:

- Providers can be changed without modifying workflow control flow.
- A model sees only the capabilities and context granted to its activity.
- Invalid output cannot advance the workflow without an explicit repair or
  failure transition.

## Phase 6 — Coding workflow vertical

Use a coding harness as the first demanding product integration.

Reference workflow:

```text
AnalyzeRequest
→ InspectRepository
→ Research?
→ Architecture
→ Approval?
→ Decompose
→ ImplementTask ↔ Build/Test/Fix
→ IntegrationBuild
→ IntegrationTest
→ Review
→ Correction loop
→ FinalValidation
→ Complete
```

Required activities include:

- Repository search and file reading
- Scoped file modification
- Build and test execution
- Diff and requirement review
- Architecture and implementation planning
- Human approval
- Git inspection, with commits and pushes as separately privileged capabilities

Deliverables:

- Coding-specific activity catalogue
- Workspace revision and evidence binding
- Reference methodology artifacts
- Recovery tests that terminate the process during every major phase
- Multi-model comparison harness
- Metrics for completion, correction loops, policy violations, cost, and latency

Exit criteria:

- A weaker model may produce worse artifacts but cannot skip required phases.
- Any source change invalidates older build/test/review evidence as configured.
- The harness can resume after process loss without duplicating committed work.
- At least three real repositories complete representative tasks successfully.

## Phase 7 — Methodology compiler

Compile natural-language methodologies only after the IR and validator are
stable through hand-authored use.

Compilation loop:

```text
Methodology + activity catalogue + workflow schema
                ↓
          Candidate artifact
                ↓
        Deterministic validation
          ↙               ↘
 diagnostics             valid
     ↓                      ↓
 bounded repair      human preview/approval
                            ↓
                    immutable artifact
```

Deliverables:

- Compiler-facing schema and constrained generation format
- Deterministic validation and correction protocol
- Semantic-diff view between methodology and artifact
- Explicit unresolved-ambiguity reporting
- Human approval and artifact signing
- Regression corpus of methodologies and expected laws

Exit criteria:

- The compiler never creates activities outside the supplied catalogue.
- Hard requirements can be traced from source methodology to artifact policy.
- Ambiguous or contradictory requirements stop for clarification.
- Repair cannot silently delete, weaken, or downgrade a policy.

## Phase 8 — Operations and ecosystem

Deliver production-quality operation around compiled workflows.

Potential work:

- Artifact registry and compatibility tooling
- Read-only workflow visualizer
- Policy and evidence inspector
- Intervention queue for runs requiring attention
- OpenTelemetry dashboards
- ASP.NET Core operational endpoints
- Hosting documentation and startup validation that make the required
  `IWorkflowStore` registration explicit
- Additional durable stores based on demonstrated demand
- Signed activity catalogues and artifact supply-chain metadata

## Solo integration questions

The architecture should leave these decisions open until Solo's plan is
reviewed:

1. Is Solo the coding harness, the methodology authoring product, or both?
2. Which component owns workspace isolation and capability enforcement?
3. Does Solo execute Zhinu artifacts directly or through an application service?
4. Where do model-provider selection, budgets, and credentials live?
5. Which state is authoritative for user-visible sessions: Solo conversation,
   Zhinu run state, or an explicit mapping between them?
6. How are human approvals represented and authenticated?
7. Which coding activities are Solo-specific versus reusable packages?
8. How should a user inspect, edit, approve, and version a compiled methodology?

The intended seam is:

```text
Solo: user experience, workspace, coding tools, approvals
                        ↕
Zhinu artifact + capability + evidence contracts
                        ↕
Zhinu: validation, durable orchestration, enforcement, audit
```

This boundary is provisional and should be refined jointly with Solo's product
and architecture roadmap.

## Near-term priorities

Before beginning natural-language compilation:

1. Write two hand-authored `WorkflowArtifact` examples.
2. Define the minimal IR and its canonical JSON representation.
3. Define activity identity, versioning, schemas, and trust levels.
4. Implement and prove the code-first durable state-loop primitive, including
   iteration identity, fenced state commits, restart invalidation, limits, and
   process-loss tests, before adding loop syntax to an authoring DSL.
5. Implement graph, type, and mandatory-gate validation.
6. Prototype revision-bound build and test evidence.
7. Integrate one restricted coding activity executor.
8. Exercise the design through Solo before expanding the grammar.

## Explicit non-goals for the first compiled-workflow release

- Arbitrary dynamic graph mutation
- General-purpose scripting as the primary authoring model
- Claims of formal verification for semantic quality
- Trusting activity metadata as a security boundary
- Embedding every model provider in the core runtime
- Replacing the code-first workflow API
- Competing immediately with multi-region workflow platforms

## Success measures

- Percentage of completion paths statically validated against mandatory gates
- Number of runtime capability violations prevented
- Percentage of evidence linked to the exact artifact/workspace revision
- Crash-recovery success across all durable phases
- Cross-model consistency in following the same methodology
- Reduction in skipped build, test, review, and approval stages
- Time required for a developer to author and diagnose a compiled workflow
- Adoption of Zhinu for useful non-AI workflows as well as AI workflows
