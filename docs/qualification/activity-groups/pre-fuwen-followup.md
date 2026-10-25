# Pre-Fuwen review: follow-up fixes and projection measurements

Date: 2026-10-19. Base: `670a3efb1560b3b231234dddb11638df18ddce40`.
Source: `7a5f87637ac68c877b4e2f933b829d67f3703c4c`.
Status: **passed**. All 1,897 full-suite tests, formatting and both package
consumer probes passed on Windows, .NET 8/10; benchmark comparison passed on
.NET 10. Packages remain unpublished.

This closes the actionable runtime follow-up from the
[original review](pre-fuwen-review.md). Fuwen package adoption, admitted macro
metadata and per-child lowering remain ZG-6 implementation requirements.

## Fixes

- Snapshot-owned indexes prepare current revisions, wait/approval/dependency
  lookups, group children, current item admissions and historical selection once.
  Each group read visits its own subtree and selected members. The public pure
  reducer still validates every caller-supplied input; only the internal path
  receiving facts from the same frozen snapshot bypasses repeated preparation.
- Index references, maps and lookup overhead are conservatively charged to
  `MaximumCacheBytes` before allocation/admission. An individual entry that
  cannot fit returns `LimitExceeded`; eviction, expiry and disposal release its
  data and indexes together. No subtree copies or query-result cache accumulate.
  Authorization and run existence/incarnation checks still run on every request.
- Optional providers must supply the actual digest of each retained fan-out
  closure vector. The reader reconstructs the existing canonical serialized
  entries from selected item keys and retained step revisions, preserving vector
  order. Wrong digests cannot establish effective closure. This extends the
  existing SQLite integrity check to the reader boundary.
- Unicode fan-out snapshot inspection previously assumed canonical plan order
  matched SQLite row order. .NET ordinal comparison uses UTF-16; SQLite BINARY
  comparison uses UTF-8, so a supplementary character and a private-use BMP key
  expose a different ordering. Snapshot validation now matches plan items by key.
  Durable item/step/occurrence identity, plan bytes and stored closure order/hashes
  retain their existing representation.

Luna supplied a focused read-only review, identified the digest gap, and checked
the implementation's snapshot ownership, history and cache accounting. No public
API, schema, scheduling, authority or execution-transition changes are introduced.

## Measurement

`ActivityGroupStateReadBenchmarks` executes a real SQLite workflow containing a
root and one work step per child group, then uses its frozen snapshot through an
in-memory provider. One operation queries every occurrence and verifies Completed
status and work-count conservation. This isolates projection CPU/allocation cost;
SQLite run-stamp I/O, capture/preparation cost and Fuwen compilation are outside
the timed sweep. It is not an end-to-end Fuwen throughput claim.

Windows x64, .NET 10.0.12, BenchmarkDotNet 0.14.0, in-process toolchain; one launch,
one warmup and three measured iterations per size. Same harness and fixture shape
before/after. Local short-run measurements have limited statistical precision.

| Item groups (+ one root) | Before mean | Indexed mean | Before allocated | Indexed allocated |
| --- | ---: | ---: | ---: | ---: |
| 32 | 288.8 µs | 31.80 µs | 587.78 KiB | 78.20 KiB |
| 256 | 14,590.6 µs | 245.82 µs | 34,387.19 KiB | 629.01 KiB |
| 1,024 | 259,450.7 µs | 1,035.19 µs | 586,411.07 KiB | 2,545.12 KiB |

At the largest measured size the indexed sweep is about 251× faster and allocates
230× fewer bytes. The result supports sharing immutable indexes through
the existing API. A future batch API requires separate measurements including
database stamps and host policy costs. Representative Fuwen plans remain a ZG-6
measurement gate; this fixture does not qualify additional dynamic composition.

Reproduce the indexed measurement:

```powershell
dotnet run --project tests/Penghou.Zhinu.Benchmarks -c Release --no-restore -- --filter '*ActivityGroupStateReadBenchmarks*' --inProcess --warmupCount 1 --iterationCount 3 --launchCount 1 --artifacts artifacts/group-state-reproduction
```

The baseline used the pre-change runtime and the added harness. The ledger
retains baseline/indexed CSVs, logs and measured binaries. Measurements were
compiled before the final source commit; retained binary hashes distinguish them
from the later exact-commit qualification builds. The harness source is included
in the source commit above.

## Qualification and remaining gates

New coverage includes valid/invalid closure digests; index cache admission;
equivalence to the public reducer for all six step statuses; history, external
dependencies, ready waits, parent counts, frozen provider collections and
concurrent reads; and Unicode fan-out inspection through the real SQLite reader.
Existing process-loss and compatibility gates remain required.

The final full suite passed **1,897 executions** with no failures or skips on
.NET 8/10, including **20 new executions** over the prior 1,877. Core tests
contain nine new cases per framework; SQLite adds the Unicode case per framework.
The final source passed repository-wide formatting verification.
Seven candidate packages identify this exact source; all 14 packaged framework
binaries match the qualified build. NuGet-only and precompiled legacy consumers
passed on .NET 8/10, including schema 5→9 migration and old-worker rejection on
schema 9. The ledger retains 11 TRX files, 50 query/projection receipts and both
measured benchmark variants with their raw CSVs/logs and binaries.

The [follow-up ledger](pre-fuwen-followup.json) supplements earlier exact-source
records. Schema remains 9; no aggregate state is persisted. Active arbitrary
code-first/loop groups retain unknown totals until authoritative closure.
Fuwen integration, wider mixed composition, Linux/remote CI and publication
remain separate gates.
