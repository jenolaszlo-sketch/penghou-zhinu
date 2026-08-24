# Architecture & quality review — findings

Reviewed: 2026-08, branch `main` @ `010d515`+ (declarative vertical), 518 tests green,
format clean. Read-only audit; no code changes accompany this document.

Scope: whole solution, with emphasis on the new `Declarative/` vertical.

## Summary

The runtime is in strong shape: layering is enforced by tests, durability
invariants live in state machines and database constraints, the exception
taxonomy / idempotency / semantics docs are real, and the minimal declarative
vertical compiles, executes, restarts, and inspects end-to-end. The findings
below are refinements — ranked by leverage — not cracks.

## A. Architecture & boundaries

### A3. Core pipeline coupled to declarative fingerprints

`Execution/Steps/RunExecutionPipeline.cs` (`ValidateRunTypes`) checks
`WorkflowRun.DefinitionFingerprint` against `IWorkflowRegistration`.
Nullable/additive, so code-first workflows are unaffected — but the generic
execution core now references a declarative concept.

**Opportunity:** an optional definition-identity validator hook invoked by the
engine keeps the core free of declarative types.

### A4. No coordination contract for multiple engines over one store

Nothing prevents constructing two engines over one store in production code;
`LeaseRecoveryScheduler.RecoverExpiredLeasesIfDueAsync` is unsynchronized
(`lastLeaseRecovery` is a plain non-volatile field) so concurrent callers can
both sweep. Recovery is idempotent — wasteful, not harmful — but the throttle
is advisory only.

## B. Coding / OOP / best practices

### B2. Object-based activity dispatch loses type safety

`ActivityExecutor<TInput,TOutput>.ExecuteAsync(object?)` casts `(TInput)input!`;
the `JsonElement → TInput` conversion is hand-rolled in `DeclarativeWorkflow`.
Contract is implicit and unenforceable at compile time.

### B3. `WorkflowCanonicalizer` recreates `JsonSerializerOptions` per call

Both overloads build identical options locally — contradicting the established
`ZhinuJsonDefaults` centralization. The two overloads are also near-duplicates.

### B4. Compiler builds a throwaway compiled definition to canonicalize

`WorkflowCompiler.Compile` constructs a temp artifact with empty fingerprint.
Also two canonical forms exist (source omits contracts; compiled includes
`TypeId`s) and `RegisterDeclarative` **recomputes** the fingerprint the compiler
just computed. Defense is fine; carrying it would be tighter.

### B5. `ActivityReference` has dual construction idioms

`required` init properties *plus* a `[SetsRequiredMembers]` constructor. Pick
one style (object-initializer matches the rest of the model).

### B6. Validator does O(n²) scans and double-resolves

The contract loop calls `definition.Steps.Single(...)` (linear scan) and
`catalogue.Resolve(...)` twice per step despite having built a descriptors map.

### B7. `IsValid` duplicated

`WorkflowValidationResult.IsValid` is hand-set; `WorkflowCompilationResult.
IsValid` derives from `Compiled is not null`. One truth source preferred.

### B8. `WorkflowRegistration.DefinitionFingerprint` invokes the factory via `Lazy`

Cheap for declarative workflows, but DI-provided factories get an instance
constructed on first fingerprint access (at start/validate time).

## C. Robustness edges

### C2. In-process post-init corruption invisible to readiness

Once initialized, `EnsureInitializedAsync` short-circuits, so dropped tables do
not fail `/readiness`; only a fresh store instance detects schema damage.
Documented; if operationally relevant, add an optional deep probe.

### C3. Minor

- `EnumerateRunsAsync` issues one extra empty page for exact-multiple results.
- `SubscribeAsync` leaves the losing poll timer to expire after wakeup.

## D. Usability

### D1. Catalogue registration ergonomics

No `Register(name, version, activity)` overload, no
`ActivityReference.Parse("research@1")`, no DI-friendly registration.

### D2. No authored-JSON path

`DeclarativeWorkflowDefinition` records deserialize fine, but there is no
`Load(json)` → validation-result helper proving the "understandable without
executable C#" claim in practice.

### D3. No one-shot compile-and-register

Compile → validate-fingerprint → register spans two APIs; a single
`RegisterDeclarative(source, catalogue)` convenience would collapse it.

### D4. No declarative inspection snapshot

Inspection works via raw run/steps/events queries; assembling fingerprint +
step→activity + status + result is manual.

## E. Remaining roadmap items (tracked elsewhere)

- Definition identity: done (fingerprint persisted + enforced).
- Solo integration proof: deferred (requires package publish).
- Benchmarks baseline: harness exists, numbers TBD.

## Priority if acted upon

1. Introduce an optional definition-identity validator seam so the core
   execution pipeline does not own declarative fingerprint policy.
2. Unify `IsValid`; share frozen JSON options; reuse validator maps.
3. Ergonomics batch: `ActivityReference.Parse`, `Register(name, version,
   activity)`, `Load(json)`, compile-and-register, inspection snapshot.
4. Tests: source-vs-compiled canonical stability.
