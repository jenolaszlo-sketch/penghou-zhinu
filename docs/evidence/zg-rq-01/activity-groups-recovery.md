# Q2: activity-groups durability and recovery

Verdict: **PASS for the qualified selected profiles** (the profiles recorded by
the ZG-2..ZG-7 and Q33 qualification records). No claim is made for the
explicitly rejected/deferred forms (nested/mixed dynamic DSL scopes, evolving
DSL selection, fan-out item waits/checkpoints/merges, generic branch retirement
beyond explicit replacement, group controls), which remain preflight rejections
or open work under the existing contract.

## Fresh executions at the bound candidate (`f0edf17`, Windows, 2026-10-26)

- Full suite: 1,968 combined .NET 8/10 executions, 0 failed (984 per
  framework: 715 SQLite, 221 core, 29 Hosting, 13 Agents, 5 AspNetCore, 1
  OpenTelemetry), including the 39 activity-group test files of the SQLite
  suite.
- Focused group/durability receipt: **241 group cases per framework passed**
  (`FullyQualifiedName~ActivityGroup`), including the process-level files
  `ActivityGroupAtomicRecoveryTests`, `ActivityGroupBranchReplacementTests`,
  `ActivityGroupChildRecoveryTests`, `ActivityGroupFanOutRecoveryTests`,
  `ActivityGroupForkRecoveryTests`, `ActivityGroupNestedTests`,
  `ActivityGroupQueryProcessTests`, `ActivityGroupRecoveryTests` and
  `ActivityGroupSelectionTests`, which spawn and hard-terminate separate worker
  processes through the existing `ProcessProbe`.
- CI at the candidate: both OS jobs green
  ([40190759599](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/40190759599)).

## Q2.1 interruption-point coverage

| Interruption point | Evidence (process-level unless noted) |
| --- | --- |
| Before group dispatch | `ActivityGroupAtomicRecoveryTests` + ZG-2/3a record: hard exit (93) during manifest installation leaves no run/manifest/start event and resumes cleanly ([zg2-3a.md](../../qualification/activity-groups/zg2-3a.md)) |
| After group dispatch begins | ZG-2/3a: hard exit inside the membership transaction rolls back the body step and both ancestor rows, retains the admitted manifest, then resumes; no lost occurrences |
| After partial completion | Q33 record: kill during partial replacement work; a fresh worker with a deliberately different selector reuses the historical decision and preserves completed effects, step IDs, occurrence IDs and parentage ([q33.md](../../qualification/activity-groups/q33.md)) |
| During result persistence | ZG-2/3a membership-transaction exit; `ActivityGroupAtomicRecoveryTests` at the candidate |
| During selection-epoch transition | Q33 record: kills after new-selection commit, inside retirement/cancellation before transaction commit, and after admission before dispatch; transaction loss leaves neither retirement nor replacement enumeration; post-commit loss retains both |
| After group completion, before caller continuation | ZG-3b nested-repetition process-loss cases; `ActivityGroupNestedTests` at the candidate ([zg3b3 record](../../qualification/activity-groups/zg3b3.md)) |

These are controlled process terminations (separate worker processes, real
hard exits, fault-injection decorators), not in-process cancellation and not
timing sleeps.

## Q2.2 native fan-out

Stable occurrence identity is frozen by byte-level vectors
(`ActivityGroupOccurrenceGoldenTests`, `ActivityGroupManifestGoldenTests`);
scheduled/running/completed/failed occurrence distinction, deterministic
aggregation, per-item failure, cancellation and unchanged retry behavior are
covered by `ActivityGroupFanOut*Tests` at the candidate and the ZG-3b.1 record
(bounded native fan-out at an explicit completed selection). Index-keyed item
identity and source-order semantics are contract-documented; deviations from
ordering are only the contractually permitted ones.

## Q2.3 selection epochs

`ActivityGroupSelectionTests`/`ActivityGroupSelectionEpochTests` at the
candidate; ZG-3b.4 record (evolving selection across explicit immutable root
epochs) and the Q33 record: concurrent replacement admission has exactly one
winner, losing admission rolls back all rows, competing or closely timed
completions derive their decision from durable state, and a different worker
scheduling order cannot produce a contradictory durable outcome (the
replacement reuses the persisted decision).

## Q2.4 execution-guarantee boundary

The probes assert durable-store state, not just API values: completed
occurrences are recognized exactly once (no duplicate logical completion —
verified by attempt counters and occurrence identity across restart), while
external side effects remain at-least-once by contract. The ZG-7 Q45 record
demonstrates the boundary explicitly: killing the first worker after operation
one commits and before operation two preserves the first boundary and its
external effect receipt without repeating the committed effect on recovery
([zg7.md](../../qualification/activity-groups/zg7.md)).

## Q2.5 failure injection

Real process termination via the existing ProcessProbe/exit-93 hard-exit
triggers and fault-injecting store decorators; every scenario asserts on
durable rows (steps, occurrences, memberships, waits, events) read through a
fresh worker after restart. No test in the cited set uses sleeps as its
primary proof mechanism.
