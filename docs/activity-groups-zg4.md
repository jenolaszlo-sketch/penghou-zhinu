# ZG-4 bounded hierarchy queries

Status: implemented and qualified at `f911630e52743373ced9f5be0e8d33e1142e63b2`.
The [qualification record](qualification/activity-groups/zg4.md) retains the
1,749-test clean full run, formatting and isolated package/binary probes.

The optional snapshot repository captures safe structural facts in one SQLite read transaction. A standalone reader needs no workflow registry or executor. Definitions and entered occurrences remain separate planes. Child workflow links retain actual native ownership and require their own snapshots; their trees are never merged into the caller's tree.

Snapshots have opaque reader-local tokens, finite lifetime, row/byte budgets, and bounded cache admission. Signed cursors bind the snapshot, query, parent, history option, and page size. Eviction, expiry, changed run incarnation, and invalid cursors require explicit resynchronization. A current run stamp checks existence/incarnation only; pages always use the captured facts, including snapshot-only generation changes that need not advance the event watermark.

The trusted embedded host may supply a read authorizer, evaluated on every request. Tokens grant no permissions. Provenance and source references require explicit host options and permissions. Payloads, authority bindings, authorization contexts/results, signal names, disposition actor/reason, operation payloads, and activation previews are excluded from the projection.

Default limits: 262,144 projected rows, 32 MiB per capture, 8 cached snapshots, 128 MiB total cache, two-minute lifetime, 100 rows per page (maximum 1,000). Source queries preflight row/string sizes before materializing fields. The reader also accounts for serialized safe facts and row overhead, freezes nested collections, and rejects inconsistent provider graphs.

The [pre-Fuwen follow-up](qualification/activity-groups/pre-fuwen-followup.md)
adds snapshot-owned aggregate indexes. Their retained memory is charged to the
total cache budget in addition to the frozen DTO budget, so a snapshot whose
required indexes cannot fit returns `LimitExceeded` before cache admission.

This slice adds no schema, identity, scheduling, aggregate lifecycle, progress reducer, or Fuwen behavior. Qualification covers immutable pagination, concurrent writer consistency, historical membership, process-independent inspection, permission downgrade, resource limits, and package compatibility.

## Host usage

```csharp
using var reader = new WorkflowGroupReader(reopenedStore);
// Or engine.CreateGroupReader(options, hostReadAuthorizer).
var opened = await reader.GetRunGroupSnapshotAsync(runId, cancellationToken);
if (opened.Status != WorkflowGroupReadStatus.Success) return;
var token = opened.Value!.SnapshotToken;
string? cursor = null;
do
{
    var page = await reader.QueryGroupChildrenAsync(runId, token,
        pageSize: 100, cursor: cursor, cancellationToken: cancellationToken);
    if (page.Status == WorkflowGroupReadStatus.ResyncRequired) break;
    if (page.Status != WorkflowGroupReadStatus.Success) return;
    Render(page.Value!.Items);
    cursor = page.Value.NextCursor;
} while (cursor is not null);
```

Capture a new snapshot after resync; replace the old view as a unit. Do not append fresh pages to the old view. Tokens are local to the reader instance and do not survive process restart. A durable sequence is an event watermark, not a unique snapshot identifier: generation/disposition and other snapshot-only changes may share it. One capture runs at a time per reader; page calls use immutable cached facts. The row/byte caps apply to the complete capture, so a large run returns `LimitExceeded`, never a silently truncated hierarchy.

`QueryGroupDefinitionsAsync` pages immediate definition children (null means definition roots), including definitions that have never entered. `QueryGroupChildrenAsync` pages immediate occurrence children. At the run root it also includes current ungrouped steps and native child-run links. `QueryGroupMembersAsync` returns direct memberships; null selects ungrouped steps, and `includeHistory` adds retained revisions. Work/Control roles and failed control steps remain visible. Child links preserve actual native parent IDs even when a fork reuses a source run's child.

Fan-out expansions expose all selected items, exact boundary revisions, historical closure vectors and effective current-revision closure. This is structural evidence, not an aggregate lifecycle or a percentage. The reader observes retained history and never synthesizes occurrences for unentered definitions.

Definition children sort by ordinal then key. Occurrences sort by definition
ordinal/key, canonical coordinate text, then occurrence ID; this is stable
structural ordering, not a chronological ordering of iterations. Root ungrouped
steps follow occurrences, then child-run links. Members sort by step key and
revision; expansions sort by fan-out key and boundary revision. Cursors bind the
page size as well as the query and parent/history selection.

The default constructor trusts the embedded host. Services exposing this surface to remote callers must provide `IWorkflowGroupReadAuthorizer`; execution authorization does not grant read access. Set `IncludeProvenance` and `IncludeSourceReferences` explicitly when needed. Privilege loss invalidates a privileged snapshot. Limits are finite deployment controls, not a performance claim at every maximum. Capture reads the complete bounded graph; pages do not hold a SQLite transaction open.
