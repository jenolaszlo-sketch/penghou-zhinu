# Q33: explicit durable branch replacement

Status: implemented; qualification is recorded in
[Q33 evidence](qualification/activity-groups/q33.md). The ZG-7 preview.9 evidence and immutable
Fuwen pin remain unchanged. This is a new native capability, not a claim that
arbitrary C# conditionals or the current Fuwen DSL imply branch retirement.

A branch can be represented by one selected keyed item whose body contains
independently durable operations. A controlling step returns the selected branch.
Changing that decision requires a new revision of the same controlling step and
a distinct stable fan-out scope key. The replacement binding explicitly names
the previous scope with `ReplacesFanOutKey`.

```csharp
new ActivityGroupFanOutBinding(
    new ActivityGroupMembership("branch", ActivityGroupMemberRole.Control),
    "select", root)
{
    ReplacesFanOutKey = "branch.a"
}
```

Use that binding on a new scope such as `branch.b` after an explicit restart of
`select`. The selection result and scope key are durable application identities;
they must not come from a process-local counter or the selector's current output
on recovery. Returning to A later uses a fresh scope such as `branch.a-again`.
The same decision value may reappear without reusing its former occurrence.

Admission must, in one transaction under the current run lease:

- validate the current completed controlling revision, immutable manifest and
  replacement plan, and require a newer revision of the previous controlling key;
- reject replacement of an already retired scope, self replacement, reuse of an
  old scope key, or replacement across different group roots;
- retain all prior selections, occurrences, membership, completed results and
  closure receipts;
- append an explicit retirement receipt linking old and new expansions;
- cancel unfinished materialized members of the retired scope, clearing their
  leases, and preserve completed/failed/cancelled members;
- admit the new selection before any new callback is dispatched.

Unmaterialized old selected items are retired by the expansion receipt. A retired
scope cannot dispatch or complete again. Replaying the exact replacement is
idempotent at the same controlling revision. Restarting the controlling step
again requires a fresh replacement scope, even when the branch value is unchanged.
Omitted scopes without a receipt remain obligations, as before.
Retirement is not compensation and does not claim to undo external side effects.

Queries retain historical rows and identities. Retired roots and descendants
have historical state, never current pending work. Ancestor progress counts only
the current selection; unretired historical selections remain conservative.
Frozen snapshots do not change. Selection, retirement, and new membership must
survive loss before admission, inside the admission transaction, after admission
but before dispatch, and during partial replacement execution.

The existing keyed execution coordinate and occurrence hash are unchanged.
Durable retirement requires additive schema 10 so older workers reject a database
whose completion obligations they cannot interpret. Providers must explicitly
advertise replacement support; the optional binding must never be silently
ignored. Forking replacement-bearing runs is rejected until separately qualified.

Fuwen lowering, general branch-control APIs, release publication and replacing
arbitrary unrelated scopes are outside this checkpoint.
