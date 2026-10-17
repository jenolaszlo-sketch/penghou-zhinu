# ZG-3b.4: evolving selection through immutable durable epochs

## Implementation note — 2026-10-17, before coding

Verified clean `codex/zhinu-activity-groups` at
`9be3308555e2fe2c4cc169391a4baf6cc32a8d91`. The previous checkpoint qualified
fixed nested repetition with 1,531 tests and schema 9.

**Nested repetition required no runtime algorithm, schema, or identity redesign.**
Preserve this exact finding in ZG-3b.6 consolidated qualification.

Inspected the root grouped fan-out runtime, v2 coordinate/plan encoder, immutable
expansion/item/closure repository, schema-9 tables and triggers, replay/restart
checks and the existing fan-out/nested/fork qualification contracts.

The accepted fan-out coordinate includes its native fan-out scope key, key/index
kind and item token. It intentionally contains no mutable selection position or
payload identity. The selection is anchored to a completed explicit ordinary
selection step, and all selected occurrences/items are admitted atomically before
member claims. Native step replay restores the selection output rather than
running its callback again. Retrying an item retains its revision/occurrence;
selective restart retains historical memberships.

The minimum bounded profile is **evolving sets across explicit immutable epochs**.
Each epoch has a stable ordinary selection step key and a distinct stable native
fan-out prefix. The existing v2 scope already distinguishes A in epoch 1 from A
in epoch 2, including identical values and reappearance. These are actual authored
native execution identities, not parsed display paths or a second identity codec.
A durable ordinary schedule step pins the epoch identities across recovery. No
new runtime API, scheduler, coordinate encoder, schema or mutable membership is
needed. In particular, this does not relax same-scope changed-plan rejection.

The contract is not an in-place diff of an existing fan-out. A selected set cannot
be expanded/contracted by reusing its old prefix after changing today's selector.
That remains rejected. Each new decision gets a separate immutable epoch, and
unchanged items selected again have distinct occurrences in that new scope.
Completed epochs remain complete history. Earlier unfinished work remains an
obligation until completed; later exclusion does not implicitly cancel or retire
it. An explicit cancellation/retirement policy and coherent active/history query
projection are outside this bounded profile. This conservative default was
presented to the user while independent inspection continued.

Qualification will use five epochs with the same group definition and identical
item payloads: `[A,B,C]`, `[A,C,D,E]`, `[C,A]`, `[A,C]`, `[B,A,C]`.
This covers expansion, contraction, reorder, completed-item disappearance,
unfinished-item disappearance and later reappearance. Two native root fan-outs
overlap while later selection is admitted, so removal is demonstrated while old
work is actually unfinished; all admitted obligations must ultimately close.
Normal C# orchestration composes the existing independent root fan-outs. No
loop/fan-out or grouped fan-out/fork combination is introduced.

Deterministic barriers will cover completed selection before admission, an
interrupted admission transaction, complete admission before first member claim,
later selection excluding unfinished work, partial member materialization,
completed obligations, reordered selection and reappearance before run completion.
All occurrences are admitted atomically; "partial materialization" here means
partial native step/membership claims, not a partially committed selected set.
Fresh crash and recovery processes must preserve every historical enumeration,
occurrence, parent, exact membership and completed result. Today's changed
selectors must not rerun completed selection callbacks, including when a completed
selection has no expansion yet. New unselected epochs may use today's selector.

Scope exclusions: automatic epoch inference from wall time, implicit rekeying,
mutating the selected set within one admitted epoch, rewriting a completed selector
revision into a different plan, automatic retirement/cancellation, arbitrary mixed
constructs, child boundaries, public hierarchy/progress and Fuwen integration.
If qualification finds the existing model cannot satisfy this explicit-epoch
contract, retain the counterexample before proposing an identity/schema change.

Selection callbacks run inside completed durable ordinary steps; item-key
projections are pure functions of their stored output. Native fan-out still
rebuilds and validates the plan on replay, including running that pure projection.
It does not memoize arbitrary external key-selector code. The original ordered
selection JSON remains in each exact selector revision. Fan-out evidence itself
sorts keyed items and has no source-order column; no new public ordered history
or active-membership projection is claimed.
