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
compensation attempt.

Remaining foundation work:

- Expand store conformance tests beyond round-trip smoke checks.
- Add stress tests for claims, leases, cancellation, and process-loss windows.
- Publish benchmark methodology and baseline results.
- Stabilize the preview API and document all transition guarantees.
- Improve administrative inspection of stuck runs and active operations.

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
- Complete the public exception taxonomy with typed workflow timeout and
  duplicate-registration/configuration failures; do not leak ambiguous raw BCL
  exceptions from normal workflow operations.
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

Loops remain a roadmap item, not part of the current linear declarative
vertical. Before implementation, define and test:

- a statically declared maximum iteration count and deterministic termination
  behavior;
- durable iteration identity so step keys cannot collide across iterations;
- whether the current iteration counter is explicit persisted workflow state,
  derived from committed iteration records, or represented by another durable
  primitive;
- restart behavior at every boundary: before the body, during the body, after
  body completion, and while evaluating the continuation condition;
- retry semantics that cannot increment the logical iteration twice;
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
4. Specify bounded-loop durability, iteration identity, limits, and counter-state
   ownership before implementing loops.
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
