# ZG-6.2b: independently durable grouped fan-out bodies

Date: 2026-10-19. Windows, .NET 8/10. Passed for the bounded keyed fan-out
profile. Full FM-2/ZG-6, Q45 and ZG-7 remain open. Packages are unpublished.

Production/package source is `fa0297b9dc21b235f27ac92b979dcf9faeb6b7b6`,
`0.2.0-preview.8`. The [evidence ledger](zg6-fanout.json) pins seven archives,
fourteen packaged DLLs, exact source identity, test receipts and package probes.
Persisted schema remains **9**. The existing v2 fan-out occurrence coordinate
and parent/identity codecs are unchanged. Qualification documentation does not
change the source identified by those archives.

## Qualified native contract

`WorkflowContext.FanOutBodyAsync` runs independent durable provider operations
through `ActivityGroupFanOutItemScope`, outside the selected item's completion
callback. Every replay visits the body; previously completed operations reuse
their durable results. The completion records only the settled body result and
depends on all registered operations. Scope sealing awaits registered operations,
including calls the body did not explicitly await, and rejects duplicate, late or
reserved operations. Descendant groups retain the selected item's parent lineage.

A typed body contract pins the immutable Fuwen execution fingerprint. The store
atomically fences claims to the current selected item, selection boundary,
lease/generation and exact group coordinate. Child closure validates revisions,
input hashes and memberships. Snapshot projection verifies that closure;
raw namespace or group claims cannot forge typed body work. The optional
`IWorkflowGroupFanOutBodyRepository` marker prevents unsupported stores from
silently ignoring body claims. `SqliteWorkflowStore` implements it.

Dependent restart preserves unaffected predecessors and sibling items while
invalidating the affected completion/closure. `StepOnly` restart of body steps
and item completions fails before writes. Existing ordinary/grouped fan-out
contracts, v1 plan bytes, authority semantics and historical keys remain intact.
Body waits and broader dynamic composition are outside this profile.

## Verification

The final regression has **1,925 passing executions** across eleven project/
framework receipts, including **696 SQLite tests per framework**. Seven new
cases on each framework cover scoped operations, claim rejection, recovery,
closure, tracked operations and restart boundaries. Formatting passes.

The seven-package consumer closure and precompiled legacy-consumer probes pass
on both frameworks. The latter migrates schema 5 to schema 9, preserves historical
rows and rejects an older worker on the newer schema. All fourteen packaged DLLs
match the exact source build. The ledger retains the complete probe receipts.

The separate Fuwen companion source is `86188500f12799bdb1d4675cdf73b513f2bcfbca`,
`0.1.0-preview.16`, against this exact native
closure. Its full regression passes **1,690 executions**, plus **12 package-only
consumer executions**. Its source/bundle/adapter/query path maps every provider
operation and preserves item parentage and source-order output. Hard process
loss after the first committed effect and before the second effect resumes from
immutable bundle bytes without repeating the first operation on either framework.
A changed persisted selection value fails before restarted provider work.
These Fuwen executions are not added to the native count.

Generated artifacts, package caches and project bin/obj directories were moved
to `D:/temp/activity-groups`, with workspace junctions retaining logical paths.
Temporary directories also use D:. Earlier static/repeat receipts, package
archives and primary consumer binaries remain separate evidence.

## Remaining gates

This qualifies one fixed fan-out scope with explicit item-local root groups,
nested static groups/macros and independently durable providers. Nested/mixed
dynamic composition, evolving-selection adapter lowering, item waits/checkpoints,
conditional value merges, grouped coordinated inference, item-local fallback and
broader closure/progress coverage remain open. Linux CI, Fuwen precompiled legacy
binaries, public package adoption and full ZG-7 are separate gates.

The [repeat checkpoint](zg6-repeat.md) and [static checkpoint](zg6-static.md)
retain their original exact-source evidence. Native dynamic qualifications do
not imply that Fuwen supports every native composition.
