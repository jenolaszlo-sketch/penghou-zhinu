# Neutral workflow authorization: delivery plan

Status: selected architectural direction and planning update, 2026-10-03.
`Penghou.Workflow.Abstractions` `0.1.0-preview.2` is published and WA-1/2/3
are complete. Zhinu ZA-2 exact-package source adoption and qualification are
complete, with its 927-case evidence retained as historical ZA-2 proof. ZA-3A,
ZA-3B and ZA-4 are locally qualified in candidate `0.2.0-preview.1`: the full
runtime matrix passed (1,017 tests: 506 on .NET 8 and 511 on .NET 10), and the
isolated seven-package consumer passed on both TFMs; the unchanged preview.15
legacy compatibility suite passed on .NET 8/10. Source delivery is pushed. Remote CI and user-run NuGet
publication remain pending. The qualification record is
[here](qualification/workflow-authorization.json), and
the runtime boundary is [documented](workflow-authorization.md). ZA-6
publication remains the user's CI step. The Hufu adapter remains held until
after that publication. Read
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
| HA-0 | Hufu | ZA-0; independent of ZA-2 | Review/reuse independent staged changes and split legacy runtime integration tests from core; preserve regression evidence and prove standalone core test/build graph |
| WA-1/2/3 | Penghou | Complete | Design, implement, qualify and publish `Penghou.Workflow.Abstractions` from Penghou CI; isolated neutral consumer and compatibility/dependency checks. Evidence is in the [adoption record](workflow-package-adoption.md) |
| ZA-2 | Zhinu | WA-1/2/3, ZA-1; complete | Exact package pin and `Agents` source/test aliases; declared-TFM matrix, API/namespace compatibility, old-binary probe and seven-package fresh-cache closure consumer qualified |
| ZA-3A | Zhinu | ZA-2; implemented and locally qualified | One effective authorizer; named no-provider profile; protected-run provider binding and direct/hosted configuration |
| ZA-3B | Zhinu | ZA-2/ZA-3A; implemented and locally qualified | Gate actual attempts before protected callback/resolver activation; denial/failure invokes zero protected callbacks; post-await dispatch validation and persisted outcome evidence |
| ZA-4 | Zhinu | ZA-3A/3B; implemented and locally qualified | Durable ApprovalRequired park/release, exact idempotent wake, restart/recovery and fresh resume/retry decisions |
| HA-1 | Hufu | WA-3, completed Zhinu phase ZA-6 | Optional `Hufu.Workflow` translation adapter referencing only Hufu and exact published Penghou.Workflow.Abstractions; independent authentication, explicit requirement mappings, decision/error/approval mapping and attributable evidence |
| HA-2 | Hufu + Zhinu | HA-0 test split, ZA-4, HA-1 candidates | Package-backed adapter/runtime tests; no-policy default, denied start, revoked retry, approved resume, compensation and crash/replay regressions. Test separation does not wait for this gate |
| ZA-5 | Integration owner | ZA-1 dispatch review for early disposition; HA-2 for any replacement evidence | ZA-5A selects legacy retention/retirement scope early; ZA-5B applies it without weakening actual-effect/start guarantees. No SQL/runtime dependency leaks into the translation adapter |
| ZA-6 | Zhinu release owner | ZA-2/3A/3B/4 and legacy compatibility qualification complete | After remote CI passes, publish the `0.2.0-preview.1` candidate through the user's CI workflow; preview.15 and WA-3 contracts remain immutable. Publication is pending |
| HA-3 | Hufu release owner | WA-3, ZA-6, HA-2; scoped ZA-5 decision | Reconfirm exact published contract adoption, inspect NuGet graphs, update release set/roadmaps, then publish Hufu packages through the user's release workflow |

WA-1/2/3 close against the published `0.1.0-preview.2` package. Zhinu ZA-2
adoption qualification remains separately documented. ZA-3A/3B/4 runtime and
durable approval and the unchanged preview.15 legacy compatibility suite are
locally qualified and pushed; remote CI and publication remain pending. Hufu workflow adapter implementation follows the published Zhinu
phase ZA-6.
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

ZA-2 is complete and ZA-3A/3B/4 plus legacy compatibility are locally
qualified and pushed; complete remote CI, then user-run ZA-6 publication.
Independent HA-0A/B remain available from
the [activity queue](authority-extension-activities.md). ZA-1 design, WA-1/2/3
publication and ZA-5A retention scope are complete; do not redo them. Hufu
HA-1/2/3 follows Zhinu ZA-6. Luban LW-1 remains a separate optional host
integration on a neutral boundary, with no mandatory Hufu/Zhinu dependencies.
Do not continue the old shared-SQLite composition as the default integration
plan. Do not remove working legacy guarantees merely to satisfy a dependency
graph. Read this plan, the original proposal, Zhinu's current execution/store
semantics and [Hufu ADR 0011](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/decisions/0011-neutral-zhinu-authority-extension.md)
before implementation. Update both roadmaps with exact gate IDs and evidence.
See [Hufu handoff](https://github.com/jenolaszlo-sketch/penghou-hufu/blob/main/docs/zhinu-authority-handoff.md) for local
staging precautions and reusable work. Each implementation and release closes
only with the corresponding evidence required by the gates above.
