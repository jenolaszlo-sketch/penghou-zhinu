# Neutral workflow authorization: delivery plan

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

Read
the [package adoption record](workflow-package-adoption.md), [Penghou release
checkpoint](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-package-release-handoff.md) and
[contract manual](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-authorization-contract.md). The [original proposal](
proposals/2026-10-03-authority-extension.md) is preserved verbatim. This plan
operationalizes it and supersedes earlier Hufu/Zhinu integration delivery order.
The 927-case qualification describes historical ZA-2 package adoption; it does
not independently qualify ZA-3A/3B/4 runtime authorization.

Current work order: [activity queue](authority-extension-activities.md). Its
ready/blocked/held statuses refine the gates below and replace older resume
instructions. The 2026-10-03 review moves independent Hufu reuse/test separation
earlier. Shared-contract publication is complete and is the prerequisite already
met by the qualified Zhinu implementation; this plan separates the Zhinu and
Hufu phases.

## Shared-contract ownership clarification - 2026-10-03

The user's clarification supersedes the original proposal's package name and
ownership. Read [Penghou's workflow contract plan](https://github.com/jenolaszlo-sketch/penghou/blob/main/docs/workflow-abstractions-plan.md):
`Penghou.Workflow.Abstractions` lives in the Penghou repository and defines
product-neutral contracts usable by alternative runtimes and authority adapters.
The contract package was published first (WA-1/2/3). Zhinu implementation and
local qualification are complete; remote CI and publication precede Hufu adapter
implementation and qualification. The archived proposal is
historical input; it is not authority to recreate a Zhinu-named abstractions package.

## Outcome and dependency rules

Penghou owns neutral workflow contracts. Zhinu implements workflow execution
and invokes those contracts; Hufu implements authority through an optional
translation adapter. Each runtime and authority implementation remains replaceable.
Resource providers still enforce actual effects. Luban remains an independent
language; this work does not change its published contracts or reopen its leaf
completion. Zhinu is not required to use Hufu with Luban or IO.

```text
Penghou.Zhinu --------------------> Penghou.Workflow.Abstractions
Penghou.Hufu.Workflow --------------> Penghou.Workflow.Abstractions
             +------------------> Penghou.Hufu
Penghou.Hufu.IO -----------------> Penghou.Hufu + Penghou.IO contracts
Penghou.Hufu.Luban --------------> Penghou.Hufu + Penghou.Luban
```

`Penghou.Workflow.Abstractions` contains contracts, bounded values and their invariants;
no engine, SQL, Hufu, Cedar, Biscuit or resource provider. The runtime owns the
default allow-all implementation. `Hufu.Workflow` must not reference Zhinu runtime,
Zhinu.Sqlite or direct workflow SQL. Hufu core/Cedar/Biscuit must contain no Zhinu
dependencies, including transitively. Do not create `Hufu.Abstractions` merely
because it appears as an illustrative name in the proposal.

The new activity preflight hook and existing final resource/mutation checks
have different guarantees. Approval of declared intent cannot authorize later
resource access or provide an atomic revocation/start transaction.

## Current inventory and migration disposition

The published Zhinu preview.15 still combines engine and contracts in
`Penghou.Zhinu`. The additive `Penghou.Workflow.Abstractions` `0.1.0-preview.2`
package is published from Penghou source `5a76b7c`; all four publishing and
verification jobs passed. Its public package contents match the exact CI
artifact excluding `repositorysignature`. A fresh-cache consumer using only
nuget.org builds and runs on .NET 8 and .NET 10, with no package dependency
other than `Penghou.Workflow.Abstractions`. Zhinu's candidate core reference is
exactly `[0.1.0-preview.2]`; its `Agents` namespace aliases in source and tests preserve the public
`MicrosoftWorkflow` signatures without a public API change. Historical ZA-2
adoption qualification passed 927 declared-TFM test cases (461 on .NET 8, 466 on .NET 10) with zero
failures or skips; the old compiled consumer and a fresh-cache seven-package
closure consumer also passed on both TFMs. Those tests preserve the ZA-2
package-adoption proof; they do not replace the current runtime qualification.
Adoption installed exact published package `0.1.0-preview.2` and did not release
a Zhinu runtime package. See the [adoption record](workflow-package-adoption.md).
Hufu core,
Cedar and Biscuit already have no Zhinu reference. The principal coupling is
the optional concrete SQLite composition and tests grouped under the core suite.

| Class | Current code or evidence | Disposition |
| --- | --- | --- |
| A: generic Hufu | Authority snapshots/requests, store/evidence/revocation contracts, Cedar/Biscuit evaluation, generic start-gate contracts | Keep in their existing Hufu projects; no workflow-runtime dependency |
| B: integration | `Hufu.Zhinu.Sqlite/ZhinuSqliteAuthorityDatabase.cs`, `ZhinuPatchStartBinding.cs`, direct generation/lease/step SQL | Freeze as legacy experimental composition. Extract translation into the new adapter; runtime state validation stays Zhinu-owned. Do not move concrete SQL into the new abstractions adapter |
| C: integration tests | `Hufu.Tests/ZhinuOperationStartTests.cs`, `Hufu.StartWorker.Tests`, their project references | Move to a dedicated integration suite; retain worker-process/replay/lease tests as regression evidence |
| Independent staged work | Bounded issuance, request admission, Hufu.Luban v2, Hufu.IO, single-patch terminal journal, package/API tooling | Review and reuse independently; do not bulk-install or publish the previous completion snapshot as this inversion |

Hufu's working tree already consumes exact Zhinu/Sqlite preview.15 packages,
qualified by 426 existing tests without sibling source. That removes source
coupling, but does not implement the new architecture. The separate completion
snapshot passed 510 tests and six local package-consumer checks; it remains
uninstalled and is not acceptance evidence for this inversion.

## Resolved contract and runtime design constraints

These refine the supplied examples under Penghou-owned, product-neutral contracts. Runtime-specific facts map into the neutral context; they do not introduce Zhinu types into shared APIs.

1. **Contract extraction and compatibility.** Inventory each candidate public
   type and its dependencies. Move only contracts needed by the neutral seam
   and independently useful workflow consumers. Preserve namespaces where
   appropriate; verify assembly identity, reflection, serialization and old
   compiled consumers. Use type forwarders where viable, or document an
   intentional preview breaking change. Never create duplicate incompatible
   definitions or move `WorkflowContext` wholesale if it drags engine behavior.
2. **Closed result categories.** In addition to Allowed, Denied and
   ApprovalRequired, model unavailable/error distinctly. Null results, unknown
   enums, exceptions and cancelled/timed-out checks never start user code.
   Define which failures suspend, fail or receive bounded infrastructure retry;
   an unavailable provider must not consume activity attempts or loop forever
   accidentally. Cancellation remains cancellation. Exact retry accounting is
   a reviewed runtime decision, not inferred from the sample enum.
   Hufu's existing request decisions are Permit/Deny/Unavailable; define the
   workflow-facing approval result through trusted approval orchestration.
   Never convert every Deny into ApprovalRequired or infer approval from a
   reason string. Bound the pending request to exact plan/requirements identity.
3. **Trusted, immutable context.** Bind actual run/step, attempt, active
   generation/revision, parent, declared requirements and context schema version.
   Deep-copy collections and bound count/byte sizes. Document canonical identity
   and case/resource semantics. Host-authenticated tenant/subject binding comes
   from the adapter's trusted host services, never arbitrary metadata or IDs.
   Unknown capability/resource schemas fail closed in a configured adapter.
   Metadata is diagnostic unless an explicitly versioned mapping says otherwise.
4. **Registration and downgrade protection.** Exactly one effective provider
   through direct construction and hosting DI. No-provider preserves existing
   unenforced behavior through the named allow-all default. Record enough
   enforcement-mode/provider identity to prevent a previously protected run
   from silently resuming with allow-all after misconfiguration. A configured
   provider failing or disappearing must never trigger fallback. Hufu itself
   gains no default permit or ambient identity resolution.
5. **Placement and runtime fencing.** Cover code-first step delegates,
   declarative activities, retries, durable resumes and compensation callbacks;
   audit direct, hosted, child-workflow and restart entry paths. Authorize after
   actual attempt identity exists and immediately before protected user code.
   Recheck Zhinu-owned lease/generation/revision after the asynchronous provider
   call, before dispatch. Do not hold a database writer transaction while waiting
   for policy or human approval. An expired/stale decision is history, not a
   dispatch token. Code outside these controlled delegates still needs resource
   enforcement; this is not native-code containment.
6. **Durable approval and evidence.** Persist bounded outcome/correlation,
   provider identity, timestamp and exact attempt/plan/requirements identity
   before dispatch or suspension. Required evidence failure blocks dispatch.
   ApprovalRequired parks without running the activity; use idempotent durable
   wake/correlation and no live lease held for human delay. A wake-up rechecks
   current authority against the current exact context. Duplicate/stale approval
   events and changed revisions cannot reuse an earlier allow. Do not put tokens
   or policy secrets in the workflow journal.
7. **Historical recovery versus new work.** Returning a completed step result
   is historical reconstruction, not another activity invocation. Any new
   attempt/resumed protected execution obtains a fresh decision. Preserve
   existing interrupted-effect/idempotency semantics and never turn recorded
   authorization into proof that an external effect completed. Separate result
   disclosure authorization where the host requires it.
8. **Legacy atomic-start boundary.** The current shared-file adapter orders
   authority/revocation, runtime acquisition and evidence in one transaction.
   A neutral preflight callback cannot claim equivalent atomicity. Freeze this
   implementation and exclude it from the new adapter's dependencies. Before
   retiring it, either prove the required start guarantees through an explicitly
   reviewed neutral runtime/provider cooperation contract, or document that the
   new activity-preflight profile does not supply them and retain the isolated
   legacy profile for existing consumers. Do not weaken resource checks or use
   sequential independent store reads as an atomic substitute.

## Ordered work, owners and acceptance gates

| Gate | Owner | Depends on | Deliverable and acceptance |
| --- | --- | --- | --- |
| ZA-0 | Zhinu + Hufu | None | This plan, source inventory and staged-work disposition; no implementation claim |
| ZA-1 | Zhinu | ZA-0 | Reviewed bounded context/result/requirements contracts, exact dispatch paths, compatibility and failure/wait/retry state transitions from decisions 1-7 |
| HA-0 | Hufu | Complete | Old snapshot reviewed by change group; legacy tests isolated with unchanged 22 cases per framework and independent core graph verified. |
| WA-1/2/3 | Penghou | Complete | Design, implement, qualify and publish `Penghou.Workflow.Abstractions` from Penghou CI; isolated neutral consumer and compatibility/dependency checks. Evidence is in the [adoption record](workflow-package-adoption.md) |
| ZA-2 | Zhinu | WA-1/2/3, ZA-1; complete | Exact package pin and `Agents` source/test aliases; declared-TFM matrix, API/namespace compatibility, old-binary probe and seven-package fresh-cache closure consumer qualified |
| ZA-3A | Zhinu | ZA-2; implemented and locally qualified | One effective authorizer; named no-provider profile; protected-run provider binding and direct/hosted configuration |
| ZA-3B | Zhinu | ZA-2/ZA-3A; implemented and locally qualified | Gate actual attempts before protected callback/resolver activation; denial/failure invokes zero protected callbacks; post-await dispatch validation and persisted outcome evidence |
| ZA-4 | Zhinu | ZA-3A/3B; implemented and locally qualified | Durable ApprovalRequired park/release, exact idempotent wake, restart/recovery and fresh resume/retry decisions |
| HA-1 | Hufu | Complete after WA-3/ZA-6 | Optional Hufu.Workflow references only Hufu and exact neutral contracts; trusted binding, finite declaration mapping, explicit approval and mandatory evidence implemented. |
| HA-2 | Integration owner | Locally qualified after HA-0B/ZA-4/HA-1 | Fresh-cache candidate Hufu packages plus exact published neutral/Zhinu packages pass 12 integration cases per framework, without project references. Approval/store recreation, revocation/retry, compensation, restart cancellation, evidence and replay are covered. |
| ZA-5 | Integration owner | ZA-1 dispatch review for early disposition; HA-2 for any replacement evidence | ZA-5A selects legacy retention/retirement scope early; ZA-5B applies it without weakening actual-effect/start guarantees. No SQL/runtime dependency leaks into the translation adapter |
| ZA-6 | Zhinu release owner | Complete | Seven 0.2.0-preview.1 packages public; both OS CI and user-run publication passed. See [exact evidence](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/qualification/zhinu-public-release.json). |
| HA-3 | Hufu release owner | WA-3, ZA-6, HA-2; scoped ZA-5 decision | Reconfirm exact published contract adoption, inspect NuGet graphs, update release set/roadmaps, then publish Hufu packages through the user's release workflow |

WA-1/2/3 close against the published `0.1.0-preview.2` package. Zhinu ZA-2
adoption qualification remains separately documented. ZA-3A/3B/4 runtime and
durable approval and the unchanged preview.15 legacy compatibility suite are
qualified and published in 0.2.0-preview.1; ZA-6 is complete. Hufu HA-0A/B
and HA-1 are complete. HA-2 fresh candidate qualification passed locally;
HA-3 remote CI/user publication is the remaining Hufu release gate.
Do not pick the next version until checking the release state. The initial Hufu core/provider packages can progress without
Zhinu; adapter completion must not become a prerequisite for unrelated Luban/IO
or Hufu core functionality.

## Required acceptance matrix

- Zhinu alone and Hufu alone build and run; no engine/policy dependency crosses
  the forbidden edges. Verify both project references and resolved NuGet assets,
  including transitive dependencies. New adapter packages require no Zhinu engine.
- No provider preserves current behavior. Configured allow executes once;
  denied, approval-required, unavailable, malformed and throwing results execute
  zero user callbacks. Missing configuration for a protected recovered run fails
  closed. Duplicate registrations are deterministic or rejected, never ordered
  authorization chains.
- Code-first/declarative/direct/hosted dispatch paths, retry, resume, compensation
  and child execution cannot bypass the seam. Fresh retries see revocation.
  Completed-result reconstruction does not dispatch work again.
- Approval survives restart, releases the worker, wakes idempotently, and
  obtains a fresh decision; stale revision, lease or generation after evaluation
  blocks dispatch. Evidence failure also blocks dispatch.
- Adapter maps exact workflow/run/activity/path/attempt/plan/parent/requirements
  identities and selected metadata. Mutation of caller collections, unknown
  requirements, empty requirements and untrusted subject metadata have explicit
  tested behavior. Hufu must not infer Allow from an empty declaration.
- Actual effects retain per-resource authorization, discovered-resource checks,
  current revocation, concrete mutation binding and outcome recovery. VFS/WhatIf
  remains future work under RA/VFS gates, not an alternate real dispatch path.
- All supported suites and isolated package consumers pass on .NET 8 and .NET 10;
  retain negative dependency/API probes and compatibility evidence in CI.

## Handoff and scope

WA-1/2/3 and Zhinu ZA-2/3A/3B/4/6 are complete and published. Resume
Hufu HA-0A/B, HA-1 and local HA-2 are complete. Close the separate
HA-3 remote CI/user publication gate. Use the [activity queue](authority-extension-activities.md)
and [Hufu handoff](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/zhinu-authority-handoff.md) rather than old held-publication prompts.

Keep the new adapter neutral and independently usable. Preserve the frozen
legacy preview.15 atomic-start profile; any ZA-5B replacement/retirement needs
separate effect-boundary evidence. Luban LW-1 remains optional host integration.
Do not bulk-install the old completion snapshot or interpret retained approval
and outcome history as fresh permission. Production identity, approval custody,
resource checks and actual mutation start remain separately qualified host duties.
