# Authority extension: current activities

Reviewed 2026-10-03 against the [canonical plan](authority-extension-plan.md),
both roadmaps, the original proposal and current source. This is the execution
queue for that plan, not another architecture. WA-1/2/3, ZA-1 design input and
ZA-2 exact-package source adoption and qualification are complete. See the
[package adoption record](workflow-package-adoption.md) and Penghou's
[release checkpoint](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-package-release-handoff.md).
ZA-0/ZA-1 design is complete. ZA-3A/3B/4 are implemented and locally qualified
in candidate `0.2.0-preview.1`: 1,017 runtime tests passed (506 on .NET 8 and
511 on .NET 10), the isolated seven-package consumer passed on both TFMs, and
the unchanged preview.15 legacy compatibility suite passed on .NET 8/10.
Source delivery is pushed. Remote CI and user-run NuGet publication remain pending. See the
[qualification record](qualification/workflow-authorization.json).
The implementation boundary is [documented here](workflow-authorization.md).

## Product-neutral ownership and sequential phases

The [Penghou workflow contract plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md)
supersedes the proposal's product-named abstractions. **Penghou owns
Penghou.Workflow.Abstractions**; it has no Zhinu/Hufu dependency and can be
implemented by other engines and authority providers. Use WA-1/2/3 for the
contract and its publication, then ZA-2/3/4/6 for Zhinu, then HA-1/2/3 for Hufu.
Runtime/adapter implementation must not intermix those phases. Source review
and independent Hufu cleanup are not completion of later integration phases.

## Work available now

| Activity | Owner | Status / dependency | Concrete output and acceptance |
| --- | --- | --- | --- |
| WA-1 | Penghou | Complete | Neutral contracts, alternative-runtime implementability, compatibility and publication scope under the Penghou-owned plan |
| ZA-1A | Zhinu | Complete | Additive neutral contract adoption; no Zhinu/Hufu type moves. Compatibility, namespace and assembly obligations are recorded in the package adoption record |
| ZA-1B | Zhinu | Complete | Bounded declarations, identity, failure/cancellation, callback-attempt accounting and explicit no-provider profile selected under the neutral contract manual |
| ZA-1C | Zhinu | Implemented and locally qualified | Code-first, declarative resolver, compensation, child start/wait and loop-predicate callback paths pass through per-attempt gates; post-await dispatch validation and mandatory evidence are implemented |
| ZA-1D | Zhinu | Implemented and locally qualified | Approval parks and releases the claim; exact idempotent wakes trigger fresh evaluation; restart/recovery, stale generation and declaration drift fail closed |
| HA-0A | Hufu | Ready, independent of ZA-2 | Reconcile the separate completion snapshot by change group. Reuse reviewed generic issuance/concurrency, IO/Luban, patch-journal and release tooling in bounded changes. Preserve the committed preview.15 package adoption; no bulk install |
| HA-0B | Hufu | Ready, independent of new adapter | Move existing Zhinu operation-start/process-worker tests into a dedicated integration suite. Prove core/Cedar/Biscuit projects and normal core test graph have no direct/transitive Zhinu dependency; preserve integration regressions and historical counts |
| ZA-5A | Zhinu + Hufu | Selected: retain frozen isolated legacy profile | Record legacy SQLite adapter retention/retirement scope and required actual-effect/start guarantees. Its implementation remains frozen. The new preflight contract must not inherit a false atomicity claim |

ZA-1 design is closed by the reviewed contract, dispatch and state-transition
decisions. The corresponding runtime paths are now implemented locally under
ZA-3/4. HA-0 is independently
complete when staged changes have a reuse disposition and the core test boundary
is qualified. Do not wait for a new Zhinu publication to start either HA-0 activity.

## Subsequent implementation and release activities

| Activity | Owner | Depends on | Output / boundary |
| --- | --- | --- | --- |
| WA-2 | Penghou | Complete; source `5a76b7c`, all four publishing/verification jobs passed | `Penghou.Workflow.Abstractions` implemented and qualified in Penghou; compatibility/neutral consumer/API/dependency checks and build/test/pack/publication CI passed |
| WA-3 | Penghou release owner | Complete; `0.1.0-preview.2` published | Neutral contract package published through user-run main CI; actual version and public-feed evidence are recorded in the [adoption record](workflow-package-adoption.md) |
| ZA-2 | Zhinu | Complete | Exact-package source adoption; 927-case TFM matrix, API/namespace checks, old-binary probe and seven-package fresh-cache closure consumer all pass. See the [adoption record](workflow-package-adoption.md) |
| ZA-3A | Zhinu | Implemented and locally qualified in `0.2.0-preview.1` | Exactly one registered effective authorizer, direct/hosted construction, explicit no-provider profile and protected-run mode/provider binding |
| ZA-3B | Zhinu | Implemented and locally qualified in `0.2.0-preview.1` | Gate acquired callback attempts; protected resolver/constructor activation follows authorization; deny/error/unavailable/malformed/throwing outcomes dispatch zero protected callbacks; post-await fence and outcome evidence |
| ZA-4 | Zhinu | Implemented and locally qualified in `0.2.0-preview.1` | Durable approval/restart/resume/retry, exact wake correlation, revocation, duplicate/stale wake handling and fresh decisions; no inherited historical Allow |
| HA-1 | Hufu | WA-3, completed Zhinu phase ZA-6 | Optional translation adapter references only Hufu + exact published Penghou.Workflow.Abstractions. Map exact identities and declared requirements; use trusted approval orchestration for ApprovalRequired, not parsing denial reasons |
| HA-2 | Integration owner | HA-0B, ZA-4, HA-1 candidates | Package-backed adapter/runtime tests from local candidate feeds on .NET 8/10; denial, approved resume, revoked retry, compensation, fencing, evidence and crash/replay cases |
| ZA-5B | Integration owner | ZA-5A; integration evidence for any replacement | Apply the reviewed legacy disposition; retain qualified atomic-start/effect guarantees or explicitly keep the isolated legacy profile. No runtime SQL in the new translation adapter |
| ZA-6 | Zhinu release owner | ZA-2/3A/3B/4 and preview.15 compatibility qualification complete | After remote CI passes, publish the `0.2.0-preview.1` candidate through the user's NuGet CI workflow with remote CI. Preview.15 and published WA-3 contracts stay immutable. Publication is pending |
| HA-3 | Hufu release owner | WA-3, ZA-6, HA-2, scoped ZA-5B | Verify exact published contract references; fresh-cache consumer/dependency checks, reviewed Hufu release set and CI. User runs Hufu publication |

The exact published package remains the Zhinu contract reference. ZA-2 remains
qualified under its historical 927-case record; ZA-3A/3B/4 are qualified in the
local runtime candidate, awaiting publication. HA-1/2 stay held until
published Zhinu ZA-6; HA-3 qualifies the later Hufu adapter release. Hufu core,
Cedar, IO and Luban package qualification can proceed after HA-0A without waiting
for ZA-6; independent packages must not acquire a Zhinu completion dependency.

## Implemented callback coverage and remaining boundaries

| Existing path | Review question / test obligation |
| --- | --- |
| `WorkflowContext.cs`: claimed `StepAsync` callback | Authorize after claim/attempt identity exists; completed-result reuse is historical and does not dispatch or reuse a prior permit |
| Class-based and declarative step resolution | Resolver/activation and user callback execute only inside the gated step callback; declarative requirements and compiled plan name/version are fingerprint-bound |
| `Execution/Outcomes/CompensationExecutor.cs` | Compensation obtains its own declaration, attempt identity and decision; forward permission is not inherited |
| Child run start/wait | `ChildRunOptions` carries separate admission and result-wait declarations. Child activities still gate independently and retain their parent execution identity |
| `WorkflowContext.Loops.cs` and loop options | Predicate is a declared durable condition callback. Loop body, loop keys/state selectors and orchestration code are outside activity preflight; nested steps gate independently |
| Approval/recovery | Approval parks and releases the claim; exact wake only triggers a new decision. Changed declaration, generation/revision, stale lease or expired Allowed outcome blocks dispatch |

This is callback preflight, not native-code containment or final resource/effect
authorization. External effects retain provider checks and existing at-least-once
idempotency/recovery semantics. Test and proof evidence is recorded in the
[workflow authorization qualification](qualification/workflow-authorization.json).

## Held or separate activities

- Bulk installation/publishing of the earlier six-package completion snapshot:
  held pending HA-0A review, not discarded and not implementation of the new seam.
- New work on direct Hufu-to-Zhinu workflow SQL: frozen legacy path; ZA-5 decides
  its disposition. The normal adapter uses the neutral contract.
- Production authenticated approval/custody, final locked mutation-start
  composition and full-host capacity: separately scoped host qualification;
  activity preflight and component tests do not close those gates.
- VFS, WhatIf, batches and stronger revocation-drain guarantees: deferred under
  existing RA/VFS/profile gates. No new IO/Luban implementation is needed for
  this contract review.

## Resume instruction

Complete remote CI and ZA-6 publication for the pushed candidate
through user-run remote CI; runtime and compatibility evidence are recorded in
[workflow-authorization qualification](qualification/workflow-authorization.json).
The exact shared-contract package and historical ZA-2 evidence remain in the
[adoption record](workflow-package-adoption.md). The user runs ZA-6 publication
after review. Do not restart completed WA-1/2/3, ZA-1 or ZA-2 work. Independent
HA-0A/B remain available; Hufu adapter work stays held until published Zhinu
ZA-6. Luban LW-1 remains a separate optional host integration.
