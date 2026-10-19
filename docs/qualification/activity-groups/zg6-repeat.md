# ZG-6.2a: native loop memberships and iteration waits

Date: 2026-10-19. Windows, .NET 8/10. This checkpoint supports Fuwen's bounded
single-level repeat profile. Full ZG-6/Q45 and ZG-7 remain open.

Production/package source is `45e831a82593048dc1ea7266cbc1e9b306d8c92c`,
unpublished `0.2.0-preview.7`. The [evidence ledger](zg6-repeat.json) pins all seven
archives, fourteen packaged DLLs, source identity, test receipts and probe output.
Persisted schema remains **9**; occurrence identity/parentage codecs are unchanged.
The qualification commit adds documentation and a test-only timing correction;
the archives continue to identify the production source above.

## Native contract

- `LoopOptions.ControlGroup` is an optional Control binding. Loop conditions,
  commits, completion and limit evidence use the enclosing execution coordinate.
  Invalid roles fail before durable claims. Authorization and dependencies are
  retained when constructing control-step options.
- `WorkflowLoopIteration<TState>.WaitForSignalAsync<T>` and
  `WaitForGroupedSignalAsync<T>` register once and enter the native wait directly.
  Condition and ambient dependencies are retained, and grouped bindings must
  match the iteration coordinate. There is no same-key enclosing step callback.
- Iteration waits preserve the JSON string input/hash contract of the historical
  ordinary iteration-step wrapper. The regression reopens an old in-flight
  wrapped claim through the new ungrouped API and preserves its step ID, revision,
  input JSON, input hash and absent membership. Root wait serialization remains
  unchanged. Existing ungrouped history is not retroactively assigned a group.
- Retry/recovery retains the same occurrence and parent identity. The native
  additions do not introduce a Fuwen dependency, group-specific identity scheme,
  schema migration or authorization policy.

## Verification

The final passing regression set contains **1,911 executions** across eleven
project/framework receipts, including 689 SQLite tests per framework. Five new
SQLite cases on each framework qualify grouped wait/recovery, incompatible
binding rejection, Control versus Work membership, invalid role preflight and
legacy in-flight wait input identity. Formatting passes.

The initial full run exposed an existing authorization test timing flake:
`DecisionExpiringDuringDatabaseWriterWait_BlocksProtectedCallback`. Its async
timer could release a SQLite writer lock only after the synchronous writer wait
had starved the thread pool and timed out. A dedicated bounded worker now
releases the lock after 400 ms, while the authorization still expires at 200 ms
and the protected callback must remain uncalled. The isolated original and
corrected cases passed; the full SQLite project was then rerun on both frameworks.
The ledger selects passing SQLite replacement receipts and retains earlier
failures separately. Production authorization code is unchanged.

Seven-package consumer closure and the precompiled legacy-consumer probe pass
on both frameworks. The latter migrates a schema-5 database to schema 9,
preserves historical rows and rejects an old worker on the new schema without
damaging history. Archive source commits and all fourteen packaged DLL hashes
match the exact production build.

Fuwen's companion checkpoint uses unpublished `0.1.0-preview.15`, production
source `cde940fa21cb4e0a117814502afb4898b9303819`. It passes 1,666 regression
executions and 10 additional package-only consumer executions. Its repeated
process-loss probe hard-kills a worker at a durable wait, queries the hierarchy
independently, re-admits immutable bytes and resumes without repeating committed
provider work. This is separate adapter evidence, not added to Zhinu's total.

## Remaining gates

Next is fan-out per-operation Fuwen lowering, with separate selection,
materialization and process-loss evidence. The adapter still rejects grouped
fan-out, nested repeats, coordinated inference and repeat-local fallback.
Broader composition, closure evidence, Linux CI, Fuwen legacy binaries, public
package adoption and full ZG-6/Q45/ZG-7 remain open. Earlier native fan-out,
fork, nested-repetition and child-boundary qualifications retain their own scope;
they do not automatically qualify Fuwen lowering.
