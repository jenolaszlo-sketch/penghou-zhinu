# Workflow store contract

`IWorkflowStore` is the service-provider interface used by the engine. A custom
implementation must preserve fencing generations, use UTC timestamps, and make
every method described as atomic a single durable transaction. A false or null
claim result must leave no partial rows or events.

Run `WorkflowStoreConformance.VerifyRunRoundTripAsync` and
`VerifyArtifactRoundTripAsync` from `Penghou.Zhinu.Testing` as the minimum
provider checks. Artifact insertion and its `artifact-published` event must be
one atomic transaction; idempotent re-publication returns the existing
reference without another event. Provider test suites
should additionally simulate concurrent claims, expired leases, cancellation,
restart fencing, compensation retries, and process loss between operation
phases.

`ForkRunAsync` must create the new pending run, copy every reusable completed
step, copy dependency edges among those steps, and append the fork event in one
transaction. Failure must leave no destination run or copied rows. Forking must
never mutate, cancel, or fence the source run.

A provider implementing `IIdempotentWorkflowRestartRepository` must bind each
operation ID permanently to the complete restart intent: workflow run, target
step, mode, actor, and reason. The generation bump, fresh pending revisions,
`step-restarted` event, and completed operation receipt are one atomic
transaction. Identical concurrent or post-crash retries return that receipt
without another transition; conflicting reuse throws
`WorkflowOperationConflictException`. The provider conformance suite treats
this capability as required for a fully conforming store.
