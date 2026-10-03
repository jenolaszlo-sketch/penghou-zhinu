# Penghou.Zhinu: pending Penghou.Hufu integration

## Superseding direction - 2026-10-03

Follow the [neutral authority-extension plan](authority-extension-plan.md).
Use the [activity queue](authority-extension-activities.md) for the current
execution order; independent Hufu staged-work review and test separation start
now. The [Penghou-owned neutral contracts](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md)
are published. Complete remote CI and publication of the Zhinu runtime phase,
then implement the Hufu adapter.
Zhinu consumes `Penghou.Workflow.Abstractions` as one workflow implementation; an
optional `Hufu.Workflow` adapter implements it with no full-runtime dependency.
Zhinu's default remains usable without Hufu. ZA-3A/3B/4 authorization and
durable-approval paths are locally qualified in candidate `0.2.0-preview.1`;
the unchanged preview.15 legacy compatibility suite also passed on .NET 8/10.
Source delivery is pushed. Remote CI and user-run NuGet publication remain pending. The candidate boundary
and unsupported orchestration/effect cases are described in the
[authorization guide](workflow-authorization.md) and
[qualification record](qualification/workflow-authorization.json). The user
runs ZA-6 publication after review. Keep HA-1/2/3 held until that package is
published. Per-resource enforcement and governed host integration remain
separate and open.

The shared-SQLite description below is legacy implementation/qualification
context. Its narrow start ordering is not the new activity-preflight gate and
does not qualify final effect authorization, terminal-outcome recovery, or a
governed host. ZA-5 governs its retention/retirement without silently weakening
existing resource checks.

Status: **Narrow co-located SQLite start adapter implemented in Hufu; complete governed host integration pending.** Updated 2026-10-01.

Penghou.Hufu is the new reusable authority library and authority-store boundary.
It now supplies bounded snapshot/Cedar/read APIs, an optional current-state/evidence
SQLite store and a separate experimental Penghou.Hufu.Zhinu.Sqlite composition.
That adapter checks actual runtime generation, revision, step/attempt/owner and
lease facts, acquires the requested handle, and records Hufu start evidence in
one shared-database writer transaction. It does not add a Zhinu core dependency,
publish a package or complete a governed mutation host.

The [start profile](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/operation-start-profile.md) chooses
block-new-starts semantics: earlier committed starts may finish after revocation
acknowledgement. Exact replay returns AlreadyStarted and cannot dispatch again.
Use one Zhinu-owned physical database for all participating repositories/Hufu
mutations; separate files and sequential lookup/AcquireAsync do not provide that
order. Standalone AcquireAsync does not create Hufu start evidence or imply Hufu
governance. Full semantic admission, provider binding and exact terminal outcome/
recovery integration remain pending. See the [qualification](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/operation-start-qualification.md).

Hufu will own reusable grants, envelopes, authority requests and decisions,
attenuation, revocation, and durable authority records. Hosts retain identity,
policy, credentials, resource resolution, and approval surfaces. Zhinu retains
execution state and recovery; existing budget services retain accounting.

## New Hufu.Workflow adapter (held until ZA-6 publication)

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
reads from two independent stores form one atomic decision. The locally
implemented Zhinu preflight gate records and fences callback intent; it does not
make final resource checks or external effects atomic with policy revocation.

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

Canonical design:

- [Hufu architecture](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/architecture.md)
- [Authority specification](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/roadmap.md)
