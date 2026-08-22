# Runtime freeze boundary for P3

Before the activity-catalogue / compiled-workflow phase, P3 may depend only on
these public runtime contracts. The compiler must not reach into
`WorkflowContext` internals, SQLite repositories, delegate-registration
mechanics, or engine implementation details.

## Allowed contracts

```text
IWorkflowRuntime (engine surface used to start/execute/inspect runs)
WorkflowDefinition (name + version identity)
Activity execution contract  (to be defined by the catalogue phase)
StepOptions
RetryPolicy
SignalDefinition<T>
IWorkflowStore semantics (store contract, not the SQLite implementation)
WorkflowRun / WorkflowStep state model
WorkflowEvent / WorkflowEventTypes
WorkflowArtifactDescriptor / WorkflowArtifactReference
Compensation / rollback contracts
Exceptions under ZhinuException
ZhinuOptions
```

## Forbidden for P3

```text
WorkflowContext internals
Delegate registration mechanics (IWorkflowRegistration internals)
SQLite repositories / IZhinuSqliteDatabase
Engine implementation details (pipeline, coordinators, outcome handler)
```

Rationale: the IR/compiler produces a `CompiledWorkflowArtifact` that the runtime
executes through the same durable machinery. If the compiler can only express
itself in the public contracts above, then a future non-SQLite store or a
hosted ASP.NET deployment cannot break it, and the compiler stays testable
against the conformance suite rather than a specific engine build.
