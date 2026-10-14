# Experimental adaptive revision runtime boundaries

The M1 revision repository is an experimental SQLite admission layer for branch manifests. A revision identifies logical work, revision-local step names, and independent Zhinu run identities. It persists the complete manifest, the proposal and its digest, authority receipt metadata, and an append-only sequence of decision evidence. This runtime layer does not verify Fuwen plans, authenticate actors, or decide whether a proposal is safe. A trusted host must do those checks before calling the mutating APIs.

## Admission and identity

Call `InitializeAsync` once to create the revision tables, then `ActivateInitialAsync` to admit a bounded manifest and its pending runs in one SQLite transaction. A branch's run must be a new independent pending execution with no child or fork lineage. The workflow identifier is stable across revisions. Revision identifiers are unique within that workflow. Logical identifiers connect work across revisions; execution identifiers identify concrete persisted runs. A preserved branch must keep the same execution identifier. A changed or newly added branch must use a fresh identifier.

The contract comparison treats work as equivalent only when its declared contract, input, authority, provenance, dependency list, and persisted workflow execution shape match. A changed prerequisite invalidates downstream dependents transitively. Completed run reuse needs explicit acceptance. Running unchanged work continues under its existing execution and lease. This equivalence check is conservative metadata comparison; the host remains responsible for ensuring the declarations truthfully describe the work.

## Proposal, approval, and commit

A proposal includes the candidate manifest, parent revision, digest of the exact active execution state, classified changes, authority decision, evidence, and impact summary. The repository recalculates the state digest and change set at proposal, approval, and commit boundaries. Progress in run status, lease generation, output, error, workflow definition fingerprint, or step history makes an earlier proposal stale. Heartbeat timestamps are omitted to avoid invalidation from lease renewal alone.

Approval is bound to the proposal digest and carries the host supplied actor and authority receipt with an expiry. A denied proposal cannot be approved. A newer proposal obsoletes the current pending or approved proposal. Rejection clears the pending pointer without activating a revision. Commit requires the exact still-pending proposal and an unexpired approval.

Commit fences invalidated branch runs by incrementing the workflow run's lease generation, clears its lease, and transitions pending or running runs to cancelled. Existing terminal evidence remains intact. The candidate's changed runs, new revision, active revision pointer, proposal state, and audit evidence commit in the same SQLite transaction. A replacement identity collision therefore aborts the entire transition. Unchanged runs are not recreated.

## Concurrency and external effects

Step claim and completion already compare the step's captured lease generation with the run's current generation. A revision commit increments that generation, so a late local step completion from the prior lease is refused by the ordinary store fence. This does not undo an external action that already happened. It also does not coordinate with a worker that bypasses the Zhinu store or shares external credentials without fencing. Hosts must use idempotency keys, provider-side fencing, or reconciliation for external effects.

The database transaction provides atomicity for one SQLite database. It does not provide distributed consensus, plan verification, authority authentication, automatic conflict resolution, or rollback of external effects. APIs are explicitly experimental and should be treated as unshipped until the full runtime and process-kill qualification is complete.

## Qualification coverage

`AdaptiveRevisionTests` exercise identity preservation and invalidation, late completion fencing, exact-digest approval, state staleness after approval, proposal obsolescence, rejection, completed-work reuse acceptance, transitive dependency invalidation, denied decisions, approval expiry, replacement collision rollback, repository reopen/idempotent commit behavior, and whole-workflow cancellation. Direct run-lease claims also prove a join stays blocked until every prerequisite run completes, independent of host scheduling. A changed Qhost execution witness invalidates an approved proposal even when run and step rows have not changed. Process termination barriers and multi-process recovery qualification are maintained separately from these unit tests.
