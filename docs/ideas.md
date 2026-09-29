# Ideas for later

These external-review proposals are not current delivery commitments. Revisit
them only with a concrete consumer and a recovery contract. Accepted follow-on
work is in the [roadmap](../ROADMAP.md#follow-on-candidates-from-the-2026-09-29-review).

| Proposal | Why it is parked | Revisit when |
| --- | --- | --- |
| Workflow state snapshots that skip method replay | A code-first workflow can hold arbitrary C# locals and control flow. Resuming in the middle of a method from a serialized snapshot would change replay and version semantics; completed steps already reuse durable results. | Replay cost is measured on a real large workflow and a constrained, versioned checkpoint form can preserve exact behavior. |
| Caller-derived step keys | File movement or a line insertion would change durable identity, invalidating replay and restart unexpectedly. Explicit keys are reviewable. | A stable source-generated identity scheme can survive ordinary refactors and migrations are tested. |
| Automatic saga compensation on any unhandled exception | Compensation can be irreversible or require operator choice. Zhinu already offers explicit compensation and rollback. | A consumer defines the exact trigger, ordering, failure, retry, and effect policy for opt-in automatic rollback. |
| Built-in cron scheduler | Recurring triggers create a separate schedule ownership, timezone, overlap, and missed-run contract. One-shot durable waits do not supply that contract. | A host needs recurring starts and the schedule policy cannot live cleanly in its existing scheduler. |
| In-flight workflow version migration | Moving a live code-first run to new code can reinterpret completed steps and local control flow. Current definition pinning and explicit restart/fork preserve history. | One reviewed migration scenario has exact state, code, evidence, and rollback mappings with a fenced transition. |
| Further split `IWorkflowStore` | The interface already composes focused repository interfaces; splitting the aggregate itself adds API churn without simplifying the engine's required store contract. | An alternative store implementation demonstrates a concrete smaller capability boundary. |

The review's AsyncLocal leak is already guarded by a `finally` and a failed-step
event regression. SQLite delays are waiting step rows, not a separate timers
table; canceled runs are excluded from runnable polling. The cancellation fix
now also marks their active wait records canceled so inspection and late
notifications see a terminal state.
