# Workflow store contract

`IWorkflowStore` is the service-provider interface used by the engine. A custom
implementation must preserve fencing generations, use UTC timestamps, and make
every method described as atomic a single durable transaction. A false or null
claim result must leave no partial rows or events.

Run `WorkflowStoreConformance.VerifyRunRoundTripAsync` from
`Penghou.Zhinu.Testing` as the minimum provider check. Provider test suites
should additionally simulate concurrent claims, expired leases, cancellation,
restart fencing, compensation retries, and process loss between operation
phases.
