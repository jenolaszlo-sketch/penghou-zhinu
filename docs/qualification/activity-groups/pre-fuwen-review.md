# Activity groups: review before Fuwen integration

Date: 2026-10-18. Review baseline: `2116cceff9012fb9e5868c14ca86208b0f404ac0`.
Report finalized: 2026-10-19; qualification evidence retains its original run date.
Fix source: `5bd107b857b16cc810530756ba5d212e32930c4e`.
Status: **passed**. Full regression suite, formatting and both package consumer
probes passed on Windows, .NET 8/10. Packages remain unpublished.

## Findings and fixes

1. **Stale approval checkpoints appeared as current approval waits (P2).**
   The group snapshot read every authorization checkpoint, while the native
   pending-authorization query excludes unready checkpoints fenced by a newer
   run or step lease. The projection could therefore retain an Approval reason
   that no longer described pending authorization. The snapshot now uses the
   native status and lease predicates, including compensation separation and
   the deliberate ready-wakeup exception across lease rollover. Four SQLite
   cases compare the group read with `HasPendingAuthorizationsAsync` and inspect
   the resulting aggregate reason. No authorization decision or runtime fence
   changes.

2. **Damaged membership could silently become ungrouped work (P2).**
   The exact LEFT JOIN dropped a membership whose run/key/revision no longer
   matched its step. Orphan memberships disappeared entirely. This could omit
   work from group counts rather than report incomplete durable data. A bounded
   existence check now rejects mismatches in both directions before reading
   steps. Four corruption probes disable foreign keys on their isolated fixture
   and prove explicit `InconsistentData`, with no partial snapshot. Normal
   foreign-key constraints remain unchanged.

3. **Provider snapshot validation was incomplete (P2).**
   Nested null collections/rows threw before validation, duplicate step IDs
   across different revisions were accepted, and expansion headers could claim
   missing items, missing boundaries or completion without closure evidence.
   Capture now rejects these before caching. Expansion checks validate selected
   identities, retained revision references and a matching current completed
   revision vector before accepting effective closure. Eight provider regressions
   cover the demonstrated failures. Invalid providers cannot turn these facts
   into successful hierarchy/aggregate reads.

4. **Repeated definition scans amplified occurrence reconstruction cost.**
   Each occurrence searched the immutable manifest linearly. The existing key
   index now stores the definitions themselves and serves occurrence, fan-out,
   fork and snapshot lookups. This removes an O(definition count) scan per lookup;
   no throughput claim is made without a benchmark. Canonical ordering, public
   API, manifest bytes and occurrence hashes are unchanged. Existing golden
   vectors and recovery tests remain the verification gates.

The pre-fix run reproduced all 14 negative cases: six SQLite failures (four
membership and two stale-lease approval cases), plus eight provider failures.
The two live/ready approval controls passed before the fix. Raw red TRX/logs are
retained separately and are not counted as passing qualification.

## Integration constraints and opportunities

- **Package adoption is real work.** The inspected local Fuwen adapter project
  still references `Penghou.Zhinu` `0.1.0-preview.14`, and its registration does
  not supply a group manifest. ZG-6 must consume an exact tested candidate package
  closure and carry admitted compiler metadata into registration and step
  bindings. These observations are about the inspected local files, not a
  qualified Fuwen commit or a claim that its entire FM roadmap is unimplemented.
- **Per-child fan-out durability remains the main integration gate.** The local
  adapter rejects nested repeat/fan-out and explains that fan-out items lack an
  item-scoped nested-loop primitive for per-operation durability. Preserve old
  item keys and behavior. Qualify versioned per-child lowering for the supported
  macro subset, or reject it explicitly; rejection leaves Q45/full FM-ZG open.
- **Closure must remain explicit.** Active arbitrary code-first/loop groups
  cannot promise exact totals or independent completion from current rows alone.
  Fuwen's admitted expansion can inform a future qualified closure contract;
  macro syntax alone is not proof that no more durable work can appear. Keep
  Unknown/Partial coverage visible during adapter integration.
- **Measure whole-tree aggregate reads before adding a batch API.** Each current
  `GetGroupStateAsync` builds indexes and validates run facts again. Requesting
  every group separately scales with group count times snapshot size. Establish
  representative Fuwen sizes and a benchmark; then consider snapshot-local
  indexes or a compatible batch capability with cache-budget accounting. This
  is a performance opportunity, not a reason to add persisted aggregate state.
- **Preserve the evidence boundary.** Windows local tests/packages do not close
  Linux CI, arbitrary mixed composition, Fuwen execution or publication. ZG-6
  and ZG-7 remain open. No identity/schema redesign was justified by this review.

## Validation

The final full suite passed **1,877 tests** on .NET 8/10 with no failures or
skips: the prior 1,845 plus 32 new regression executions. Formatting passed.
The earlier focused core/query/state checks passed 230 executions; these are
overlapping coverage, not additional full-suite totals.
Seven candidate packages retain the exact source commit and 14 matching
packaged/build binary hashes. NuGet-only consumers and precompiled legacy
consumers passed on both frameworks, including schema 5→9 migration and old-worker
rejection on schema 9. The ledger retains 11 final TRX files and 50 query/projection
receipts; pre-fix regression failures are recorded separately.

The [JSON ledger](pre-fuwen-review.json) records exact source, full-suite TRX
counts, source/package hashes and consumer probes. It supplements the historical
[ZG-5 qualification](zg5.md), rather than rewriting its source or test totals.
The review changes no public API, persisted schema, execution identity or durable
state transitions. This is a bounded code review and regression exercise, not
a proof that every possible provider or dynamic composition is supported.
