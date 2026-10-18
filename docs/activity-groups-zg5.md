# ZG-5 aggregate state and progress

Status: **qualified** on Windows, .NET 8/10 at source
`01f2ac64173a625f3bf745704391871aac014a72`: **1,845 tests**, formatting,
seven unpublished packages and both package/binary/schema probes passed.
See the [qualification and exact-source ledger](qualification/activity-groups/zg5.md).
Next is ZG-6 Fuwen metadata handoff and package integration.

The reducer is pure: selected Work/Control declarations, current/historical step
facts, dependencies, safe waits/approvals, run/generation/operation context, an
explicit extent (Open/Closed/Interrupted), coverage and a fixed observation time
produce a diagnostic projection. It performs no I/O, payload decoding or writes.
Unknown or inconsistent required data produces null status, never success.

Precedence is Failed, Running, Waiting, proven Completed, Cancelled, blocked
Pending, then ordinary Pending. Running lease expiry adds AwaitingRecovery.
Wait priority is Approval, Signal, Child, Retry, Delay, Other; readiness/due time
adds AwaitingDispatch until the step itself transitions. Work counts exclude
Control, but both roles participate in status. Current revision alone counts;
attempts, containers, retained revisions and child-run internals never add work.
Selected declarations without a materialized step count as Pending.

The additive optional reader interface evaluates one actual occurrence and its
descendants from the same frozen ZG-4 snapshot and CapturedAt timestamp. No live
facts are mixed into that projection. All existing authorization, token expiry,
incarnation checks and redaction apply. The existing structural reader interface
is unchanged, preserving third-party implementations.

Successful run completion supplies conservative materialized-tree closure.
Current immutable fan-out admission supplies exact selected membership for its
individual item occurrences, including items not yet claimed. A static ancestor
containing fan-out does not thereby acquire closure for arbitrary future work.
Other active code-first/loop groups remain Open with unknown totals until a
successful terminal boundary proves closure. No lexical Dispose/finally or step
name guesses seal a group. Non-current fan-out-only occurrences are explicitly
historical; their stale selection/closure never enters current progress.
Ancestors with unresolved superseded selections expose Partial coverage. Failed
or cancelled runs interrupt remaining expansion, rather than closing it normally.

The pure input supports explicit complete sealed empty membership for adapters
with authoritative evidence. This adds no generic durable sealing API and makes
no new native loop/branch selection claim. Existing native durability profiles,
schema 9 and occurrence identity algorithms remain unchanged. Aggregate results
do not grant acceptance/authorization or influence execution. Compensation and
generation state remain context separate from forward step outcomes.
