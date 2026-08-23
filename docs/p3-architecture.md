# P3 Declarative workflow architecture

This document describes the minimal declarative vertical proven on the durable
runtime: a sequential `A → B → C` workflow defined declaratively, compiled to an
immutable definition, executed through the existing Zhinu durability machinery,
and resumed after a process restart.

```
DeclarativeWorkflowDefinition
        ↓ compile (validate → canonicalize → fingerprint → resolve)
CompiledWorkflowDefinition
        ↓ execute (reuses WorkflowContext.StepAsync durability)
WorkflowRun
        ↓ produces
WorkflowResult / WorkflowArtifactReference
```

## Concepts

| Term | Meaning |
| --- | --- |
| `DeclarativeWorkflowDefinition` | The source/model: name, version, ordered steps. Contains no delegates, services, DI, store, or runtime handles. |
| `CompiledWorkflowDefinition` | The validated, canonical, immutable executable definition: name, version, SHA-256 fingerprint, and resolved steps (id, activity reference, dependencies, portable input/output contract identities). Serializable, no executable state or CLR `Type` objects. |
| `WorkflowRun` | A durable execution instance of a compiled definition. Retains `WorkflowName`, `WorkflowVersion`, and `DefinitionFingerprint` so recovery is exact. |
| `WorkflowArtifact` | Output produced by a run: the workflow result (`WorkflowResult`) and any durable external-artifact references (`WorkflowArtifactReference`) published by activities. |

Source vs compiled: the source model is the human/authoring contract; the
compiled model is what the runtime executes. Only compiled definitions are
registered and persisted; the source can be recompiled, but a run resumes from
its recorded fingerprint, not from source.

## Activity catalogue

The catalogue is the extension boundary between declarative definitions and
executable application code, and it separates **description** from
**implementation**:

```text
ActivityReference  (name@version, ordinal, case-sensitive)
        ↓
ActivityCatalogue  ── descriptor (input/output contracts) ── resolves exactly
                   └─ implementation (IActivity<TInput,TOutput>) ── invoked at run time
```

- Registration rejects duplicates.
- Resolution is exact by name + version; there is no "latest" fallback.
- An unknown activity produces a structured validation diagnostic before
  execution.

## Compiler pipeline

`WorkflowCompiler.Compile(definition, catalogue)`:

```
validate structure (name/version, unique ids, deps exist, acyclic, activity resolves, contracts)
canonicalize (stable property ordering, sorted steps/deps)
fingerprint (SHA-256 over canonical JSON)
CompiledWorkflowDefinition
```

Compilation is deterministic and has no LLM involvement. It either yields a
valid compiled definition or a `WorkflowValidationResult` with structured
diagnostics (`Code`, `Severity`, `Message`, `StepId`); it never yields a
partially valid artifact.

## Execution mapping

`DeclarativeWorkflow` implements `IWorkflow<JsonElement, JsonElement>` and maps
each compiled step to a durable `WorkflowContext.StepAsync` call keyed by the
step id. It does not implement a second scheduler, retry engine, lease model,
recovery model, event system, or child mechanism — it is an execution
description over the existing runtime. Steps may be retried and recovered like
any code-first step.

## Runtime boundary

The declarative layer depends on the stable runtime contracts it actually uses:
`IWorkflow<TInput,TOutput>`, `WorkflowContext`, `WorkflowRegistry`, and
`IWorkflowFingerprint`. It does not depend on `WorkflowEngine` internals, SQLite
repositories, `WorkflowContext` internals, or persistence-specific types.

## Fingerprint and recovery semantics

- A run records the compiled definition's fingerprint at start.
- On resume, the runtime verifies the registered definition's fingerprint
  matches the run's recorded fingerprint. A changed definition for the same
  name/version is rejected (the run fails with a clear serialization error)
  rather than silently replaying older durable state with new steps.
- A missing registration is rejected; there is no "close enough" or fallback
  version resolution.
- A matching registration resumes from durable step state: completed steps are
  reused, unfinished steps execute.

## Minimal example (`A → B`)

```csharp
var catalogue = new ActivityCatalogue();
catalogue.Register(new ActivityReference("research", "1"), new ResearchActivity());
catalogue.Register(new ActivityReference("implement", "1"), new ImplementActivity());

var definition = new DeclarativeWorkflowDefinition
{
    Name = "build",
    Version = "1",
    Steps = new[]
    {
        new DeclarativeWorkflowStep { Id = "research", Activity = new ActivityReference("research", "1") },
        new DeclarativeWorkflowStep { Id = "implement", Activity = new ActivityReference("implement", "1"), DependsOn = new[] { "research" } }
    }
};

var compiled = WorkflowCompiler.Compile(definition, catalogue).Compiled!;
var registry = new WorkflowRegistry().RegisterDeclarative(compiled, catalogue);
var engine = new WorkflowEngine(store, registry);
var runId = await engine.StartAsync<JsonElement>("build", "1", input);
```

`RegisterDeclarative` is the public runtime seam. It verifies the artifact's
canonical fingerprint and resolved contracts against the catalogue, then creates
the internal adapter. Consumers do not depend on untyped executors or adapter
implementation details.

## What this vertical deliberately does not include

Conditionals, loops, parallelism, generated activities, capability policy,
evidence, AI orchestration, scripting, schema migration between compiled
versions, a broad JSON Schema, a CLI, or a durable polling primitive. Those are
later P3 increments; the purpose here was to prove the compile → execute →
recover path on the real runtime.
