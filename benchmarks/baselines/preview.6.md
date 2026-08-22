# Zhinu benchmark baseline — preview.6

Run the benchmarks on a quiet machine before changing performance-sensitive code
and update the table. This is a **regression detector**, not a performance
promise: numbers will vary across hardware. The important thing is that a
refactor does not silently move the durable primitives by an order of magnitude.

## Running

```bash
dotnet run -c Release --project tests/Penghou.Zhinu.Benchmarks
```

Run a single benchmark to save time:

```bash
dotnet run -c Release --project tests/Penghou.Zhinu.Benchmarks -- --filter "*ClaimComplete*"
```

Each benchmark uses an isolated temporary SQLite database (pooling disabled) so
runs do not interfere.

## Scenarios

| Benchmark | What it measures |
| --- | --- |
| `ClaimCompleteBenchmarks.ClaimThenComplete` | Uncontended durable claim + complete of one step: the core write path. |
| `CompleteResultSizeBenchmarks.CompleteWithResult` | Claim + complete with 64 B / 1 KB / 16 KB committed results — serialization dominates here. |
| `FanOutBenchmarks.FanOutWorkflowRun` | A workflow running 10 / 100 / 1000 durable fan-out steps. |
| `ArtifactPublishBenchmarks.PublishArtifact` | Publishing one artifact reference with provenance. |
| `LeaseRecoveryBenchmarks.RecoverExpiredLeases` | Recovering 100 / 1,000 / 10,000 expired leases (discovery + sweep). |
| `HistoryGrowthBenchmarks.ReadEventPage` | Reading a 100-event page from a run with 10,000 / 1,000,000 events — history-growth degradation. |

## Baseline (preview.6, to be filled by a real run)

| Benchmark | Params | Mean | Allocated |
| --- | --- | --- | --- |
| ClaimThenComplete | — | TBD | TBD |
| CompleteWithResult | 64 B / 1 KB / 16 KB | TBD | TBD |
| FanOutWorkflowRun | 10 / 100 / 1000 | TBD | TBD |
| PublishArtifact | — | TBD | TBD |
| RecoverExpiredLeases | 100 / 1000 / 10000 | TBD | TBD |
| ReadEventPage | 10k / 1M | TBD | TBD |

## Notes

- `HistoryGrowth` is the most important: normal operation should stay roughly
  constant as history accumulates. If `ReadEventPage` degrades materially with
  1M events, the event index is the suspect.
- `LeaseRecovery` exercises the `ix_workflow_steps_runnable` and related indexes;
  large values reveal whether recovery is O(expired) or accidentally worse.
- Artifact payloads here are small references; Zhinu deliberately does not claim
  SQLite is a blob store for arbitrary artifact contents.
