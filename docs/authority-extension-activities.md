# Authority extension: current activities

Updated 2026-10-04. WA-1/2/3 and Zhinu ZA-2/3A/3B/4/6 are complete.
`Penghou.Workflow.Abstractions` 0.1.0-preview.2 and all seven Zhinu
0.2.0-preview.1 packages are published and indexed. Zhinu source commit
`2f02a2e91d87e6429fd17a3819308301ab91f17c` passed both OS jobs in
[CI 37137640422](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37137640422)
and [publication 37138352675](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37138352675).
All seven public packages were downloaded; hashes and exact repository commit
metadata are recorded in [public-release evidence](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/zhinu-public-release.json).

Hufu HA-0A/B review and test isolation are complete; HA-1 is implemented.
The optional adapter depends only on Hufu and the exact neutral contract.
The current local source suite passed 816 cases, 408 per .NET 8/10 framework
(210 core, 93 Biscuit, 19 IO, 22 legacy, 52 Workflow unit, 12 integration).
The bounded request-preflight telemetry slice adds 26 cases per framework.
A finite worker queue emits closed categories/timing without request metadata;
listener loss, saturation and shutdown preserve mandatory evidence/results. See
[telemetry profile](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/optional-telemetry.md)
and [current evidence](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/optional-telemetry.json).
The 764-case explanation checkpoint remains historical evidence. Earlier work
was committed as Hufu 42a045b, Penghou 77bac95 and Zhinu a1df6e9; the telemetry
delivery is local, with push/remote CI and user-controlled Hufu publication open.
The bounded typed-path explanation slice adds 32 cases per framework, with
actual evaluator capture and separately authorized redacted disclosure. See
[profile](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/decision-explanations.md)
and [explanation evidence](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/decision-explanations.json).
The earlier 700-case core checkpoint remains historical evidence.
Independent core admission/issuance adds 73 cases per framework and no engine
dependency; see [the profile](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/core-admission-and-issuance.md)
and [earlier core qualification](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/core-hardening.json).
The earlier 554-case workflow qualification remains historical evidence.
HA-2 fresh candidate-package qualification passed on both frameworks; HA-3
remote CI and user-run Hufu publication remain open. No Hufu package or production
host is claimed published. See the [Hufu handoff](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/zhinu-authority-handoff.md) and
[qualification ledger](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/workflow-authorization.json).

This queue operationalizes the [canonical plan](authority-extension-plan.md).

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
| HA-0A | Hufu | Complete; bounded reuse dispositions recorded | Reconcile the separate completion snapshot by change group. Reuse reviewed generic issuance/concurrency, IO/Luban, patch-journal and release tooling in bounded changes. Preserve the committed preview.15 package adoption; no bulk install |
| HA-0B | Hufu | Complete; 22 unchanged cases per framework isolated | Move existing Zhinu operation-start/process-worker tests into a dedicated integration suite. Prove core/Cedar/Biscuit projects and normal core test graph have no direct/transitive Zhinu dependency; preserve integration regressions and historical counts |
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
| HA-1 | Hufu | Complete after WA-3 and ZA-6 | Optional translation adapter references only Hufu + exact neutral contracts; trusted full-context binding, finite requirements, typed approval and mandatory evidence implemented. |
| HA-2 | Integration owner | Locally qualified after HA-0B/ZA-4/HA-1 | Fresh-cache candidate Hufu packages plus exact published neutral/Zhinu packages pass 12 integration cases per framework, without project references. Approval/store recreation, revocation/retry, compensation, restart cancellation, evidence and replay are covered. |
| ZA-5B | Integration owner | ZA-5A; integration evidence for any replacement | Apply the reviewed legacy disposition; retain qualified atomic-start/effect guarantees or explicitly keep the isolated legacy profile. No runtime SQL in the new translation adapter |
| ZA-6 | Zhinu release owner | Complete | Seven 0.2.0-preview.1 packages indexed; both OS CI and user-run publication passed at exact source commit. See [public evidence](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/zhinu-public-release.json). |
| HA-3 | Hufu release owner | WA-3, ZA-6, HA-2, scoped ZA-5B | Verify exact published contract references; fresh-cache consumer/dependency checks, reviewed Hufu release set and CI. User runs Hufu publication |

WA-1/2/3 and Zhinu ZA-2/3A/3B/4/6 are complete. The current Hufu
HA-2 package-backed qualification passed locally; HA-3 adds both OS
remote CI and user-run publication. Independent cores and IO/Luban adapters
remain independent of the runtime. Historical ZA-2 adoption evidence and the
1,017-case runtime record remain separately attributable.

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
  rejected as a bulk installation source by completed HA-0A; individual reuse/defer decisions are recorded in Hufu. It is not the new seam implementation.
- New work on direct Hufu-to-Zhinu workflow SQL: frozen legacy path; ZA-5 decides
  its disposition. The normal adapter uses the neutral contract.
- Production authenticated approval/custody, final locked mutation-start
  composition and full-host capacity: separately scoped host qualification;
  activity preflight and component tests do not close those gates.
- VFS, WhatIf, batches and stronger revocation-drain guarantees: deferred under
  existing RA/VFS/profile gates. No new IO/Luban implementation is needed for
  this contract review.

## Resume instruction

Do not repeat completed WA/ZA publication. Resume from Hufu HA-0A/B and
HA-1/local HA-2 completion, consult the [current Hufu handoff](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/zhinu-authority-handoff.md), then finish
HA-3 remote CI/user-run publication.
The six reviewed Hufu packages target .NET 8/10; no engine dependency enters
the release graph. Experimental Biscuit and frozen legacy preview.15 profiles
remain outside that release set. Preserve required evidence, fresh retry/wake
checks and production host/effect boundaries. Luban LW-1 remains separate.
