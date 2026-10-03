# Penghou.Zhinu: pending Penghou.Hufu integration

Status: **Narrow co-located SQLite start adapter implemented in Hufu; complete governed host integration pending.** Updated 2026-10-01.

Penghou.Hufu is the new reusable authority library and authority-store boundary.
It now supplies bounded snapshot/Cedar/read APIs, an optional current-state/evidence
SQLite store and a separate experimental Penghou.Hufu.Zhinu.Sqlite composition.
That adapter checks actual runtime generation, revision, step/attempt/owner and
lease facts, acquires the requested handle, and records Hufu start evidence in
one shared-database writer transaction. It does not add a Zhinu core dependency,
publish a package or complete a governed mutation host.

The [start profile](../../Penghou.Hufu/docs/operation-start-profile.md) chooses
block-new-starts semantics: earlier committed starts may finish after revocation
acknowledgement. Exact replay returns AlreadyStarted and cannot dispatch again.
Use one Zhinu-owned physical database for all participating repositories/Hufu
mutations; separate files and sequential lookup/AcquireAsync do not provide that
order. Standalone AcquireAsync does not create Hufu start evidence or imply Hufu
governance. Full semantic admission, provider binding and exact terminal outcome/
recovery integration remain pending. See the [qualification](../../Penghou.Hufu/docs/operation-start-qualification.md).

Hufu will own reusable grants, envelopes, authority requests and decisions,
attenuation, revocation, and durable authority records. Hosts retain identity,
policy, credentials, resource resolution, and approval surfaces. Zhinu retains
execution state and recovery; existing budget services retain accounting.

## Zhinu's planned integration

- Persist exact authority admission references alongside run, revision,
  node/item, attempt, and execution-generation identities.
- Check activity authority at dispatch and coordinate final checks with trusted
  resource brokers; dispatch approval alone cannot authorize later I/O.
- Add typed authority waits and idempotent, expected-state application of host
  decisions, with exact re-admission and fenced revision activation.
- Define operation-start ordering against revocation, expiry, and stale workers;
  reconcile ambiguous external effects and survive restart without widening rights.

Zhinu remains authoritative for execution, active revisions, leases/fences,
operation history, and recovery. Hufu owns live grant/decision/revocation state.
Do not infer current authorization from an old workflow receipt or assume that
reads from two independent stores form one atomic decision.

## Completion evidence

A denied operation performs zero unauthorized resource I/O; stale workers and
post-revocation starts are blocked under the documented ordering contract.
Crash/restart preserves admission identities and budget consumption, and partial
approval cannot activate uncovered mandatory work.

## Dependency and design home

Implementation depends on Hufu's reviewed contracts, durable-store semantics,
and a proven host/resource-broker enforcement path. Continue current correctness
work independently; do not add placeholder dependencies or infer security from
the existence of Hufu's scaffold.

Canonical design (links assume sibling checkouts):

- [Hufu architecture](../../Penghou.Hufu/docs/architecture.md)
- [Authority specification](../../Penghou.Hufu/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](../../Penghou.Hufu/docs/roadmap.md)
