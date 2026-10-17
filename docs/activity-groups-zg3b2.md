# ZG-3b.2: grouped native workflow forks

Implemented 2026-10-16 from the qualified fan-out checkpoint `1d3c232`.
See [qualification](qualification/activity-groups/zg3b2.md) for exact source,
frameworks, compatibility receipts and the supported profile. SQLite remains
**schema 9**; no occurrence encoding or execution-coordinate format changes.

## Native meaning and lineage

Zhinu's existing `WorkflowEngine.ForkAsync` creates a **new run** seeded from
completed reusable steps. It is not an in-run branching/join primitive. The
existing destination RunId and SourceRunId distinguish fork lineages. This slice
does not add a fork frame, group-specific fork ID, hash domain or scheduler.

For each copied grouped step, reconstruct its explicit source occurrence ancestry
from durable records, then recreate that ancestry under the destination RunId.
Preserve group keys, roles and exact existing root/loop coordinates. The unchanged
occurrence v1 hash naturally yields distinct IDs for sibling runs. Every copied
membership points to the copied destination step ID/revision and a destination-owned
occurrence. Parent pointers never cross runs. Unentered/non-reused groups are not
materialized merely because they appear in a manifest.

Two sibling forks using the same definitions have separate group histories.
Within each run, an ordinary join step retains its explicitly declared membership
(including null); dependency reconvergence cannot merge source/sibling occurrences.
Source steps, occurrences, events, lease/generation and output remain unchanged.

## Atomic admission and optional capability

All original public fork signatures and the ungrouped provider contract remain.
A grouped source or target requires the additive `IWorkflowGroupForkRepository`.
The engine validates the source's admitted manifest and requires this capability
before any destination writes. The SQLite facade exposes `ForkGroupedRunAsync`.
The old low-level `ForkRunAsync` continues rejecting group-bearing runs.

One existing immediate fork transaction validates the source and target pins,
creates the pending destination, installs its target manifest, copies native reuse
candidates and dependency edges, rebinds grouped memberships parent-first, and
appends the ordinary RunForked event. Failure rolls back all destination records.
The existing first-generation binding follows commit and recovers normally if a
process dies between those boundaries.

Native reuse selection, step keys, new copied step IDs, destination revision 1,
attempt/input/output/type/implementation/declaration/timestamps, source-run lineage
and authority identity remain unchanged. No source authorization decisions or
compensation receipts become destination permissions. Reexecuted protected work
uses normal authorization again. The grouped store path requires the same provider,
host binding and authority-subject identity as the source; the engine already
enforces that continuity for native forks.

## Target manifests, mixed membership and restart

Each reused group and ancestor must retain its definition key, parent lineage,
kind and exact macro provenance. The destination may have different display names
or ordinals and additional definitions. Missing/reparented/reinterpreted reused
groups abort the whole fork, rather than flattening membership or restamping old
work. The target run has its own immutable manifest pin; annotation bytes never
replace executable definition fingerprints.

Copied null memberships stay null, including when an ordinary source forks into
a grouped target. New or invalidated target steps can acquire their declared
membership on their first claim. A target without a manifest is allowed only if
no reusable grouped membership would be lost. Invalidate affected steps through
the existing native fork boundary when changing their grouping; changing inputs
alone does not authorize a new group assignment to inherited work.

Retries keep the same revision/occurrence. Selective restart preserves historical
membership and clones it to the new revision with ordinary fencing. The existing
changed-input fork supersede path now also clones membership before validating the
new revision, retaining its occurrence while preserving the old row. Completed
sibling work remains reusable and receives no new callback.

ForkAsync remains non-idempotent: retrying a known destination ID after committed
creation produces the existing conflict. After an interrupted uncommitted fork,
the same chosen ID can be used because no destination exists. Recovery resumes
the committed run instead of inventing another branch.

## Qualification boundaries

The profile covers static nested groups, preservation of existing typed-loop
coordinates, sibling-run isolation, retries, running-branch selective restart,
cross-version changed-input supersede, mixed grouped/ungrouped steps and targets,
authority continuity and native joins. Seven separate-process barriers cover
manifest/membership/event transaction interruption, post-fork pre-generation crash,
Running work, completed work before join, and completed join before run completion.
Before recovery, persisted identities are inspected alongside an unchanged source
and already-completed sibling. Callback receipts distinguish reused committed work
from unfinished attempts.

**Forking a run with grouped fan-out evidence remains explicitly rejected.** Its
enumeration/boundary/closure records need a coherent destination rebinding policy;
copying memberships alone would break the qualified fan-out contract. This is a
composition limit, not a change to ungrouped native fan-out/fork behavior.

Next is **ZG-3b.3 nested repetition**: combine already-qualified primitives to
prove repeated macro parents and inner occurrences do not alias. Evolving
selection stays in 3b.4; child boundaries in 3b.5; consolidated dynamic gates
precede ZG-4 hierarchy queries, ZG-5 state/progress, ZG-6 Fuwen integration and
ZG-7 cross-repository qualification. No group query/progress or Fuwen adoption
is claimed here.
