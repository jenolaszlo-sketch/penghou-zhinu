# Runtime freeze boundary for P3

The activity-catalogue / compiled-workflow layer may depend only on public
runtime contracts. The compiler must not reach into
`WorkflowContext` internals, SQLite repositories, delegate-registration
mechanics, or engine implementation details.

## Allowed contracts

```text
IWorkflowRuntime (engine surface used to start/execute/inspect runs)
WorkflowDefinition (name + version identity)
IActivity<TInput,TOutput> / ActivityDescriptor / IActivityCatalogue
StepOptions
RetryPolicy
SignalDefinition<T>
IWorkflowStore semantics (store contract, not the SQLite implementation)
WorkflowRun / WorkflowStepRun state model
WorkflowEvent / WorkflowEventTypes
WorkflowArtifactDescriptor / WorkflowArtifactReference
Compensation / rollback contracts
Exceptions under ZhinuException
WorkflowAuthorizationDeclaration / WorkflowAuthorizationException
ZhinuOptions
```

## Forbidden for P3

```text
WorkflowContext internals
Delegate registration mechanics (IWorkflowRegistration internals)
SQLite repositories / IZhinuSqliteDatabase
Engine implementation details (pipeline, coordinators, outcome handler)
```

Rationale: the compiler produces a `CompiledWorkflowDefinition` that the runtime
executes through the same durable machinery. If the compiler can only express
itself in the public contracts above, then a future non-SQLite store or a
hosted ASP.NET deployment cannot break it, and the compiler stays testable
against the conformance suite rather than a specific engine build.
