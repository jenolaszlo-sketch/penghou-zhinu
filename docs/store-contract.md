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

## Optional protected-execution capability

An engine configured with `WorkflowExecutionAuthorizationOptions` additionally
requires `IWorkflowAuthorizationRepository`. SQLite implements it. A custom
store without that capability can still run the unprotected profile; it cannot
be used for configured authorization. Existing store conformance checks do not
qualify this new capability by themselves.

Run insertion, idempotent admission, child creation and fork must retain the
effective provider/profile binding. Protected claims bind immutable declarations
to their exact revision. Compensation retains its own declaration. A recovered
run must reject a missing or different host profile rather than reverting to
unprotected execution.

The repository must implement these operations with current, authoritative
store facts:

- `CommitAuthorizationAsync`: atomically validate the run and claim's owner,
  unexpired leases, generation, revision, declaration, intended attempt, context
  and provider result; persist bounded outcome evidence; record an Allowed
  dispatch marker, park ApprovalRequired, or settle a terminal denial/error.
- `ValidateAuthorizationDispatchAsync`: recheck current expiry and exact fences
  against the committed Allowed outcome immediately before callback dispatch.
- `RenewAuthorizationClaimLeaseAsync`: renew only the still-current owned run
  and claim; never revive an expired lease or an obsolete generation.
- `GetPendingAuthorizationAsync` and `HasPendingAuthorizationsAsync`: expose
  current pending state and prevent swallowed approval from becoming success.
- `WakeAuthorizationAsync`: accept an exact, idempotent approval correlation
  and mark it ready. A wake is not permission; execution constructs a fresh
  request and reevaluates policy.

Provider and human waits occur outside writer transactions. SQLite samples its
own clock after obtaining the writer lock; caller timestamps alone cannot
prove a lease or decision is still valid. The host should configure the same
trusted `TimeProvider` for engine and store. Test lost leases, changed revisions,
generation replacement, expired decisions, evidence failure, duplicate/stale
wakes and restart recovery before claiming a protected custom-store profile.

See [the authorization guide](workflow-authorization.md) for identity, bounds
and transitions. This store capability fences runtime dispatch; it does not
make policy evaluation atomic with an external resource mutation.
