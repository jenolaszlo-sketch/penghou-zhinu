# Activity groups: per-item progress after fan-out settlement

The ZG-6.2b dynamic execution profile is unchanged. Cross-repository progress
qualification found that a completed multi-operation item remained Pending while
a sibling ran, then appeared Cancelled when the run was cancelled. The reader
recognized the original one-step item closure only, or required the whole run to
complete. This follow-up changes diagnostic projection, not durable execution.

A current body item is closed only when its selected input hash matches its
completed Control step, the selection boundary/revision is current and complete,
required dependency edges are present, and completion dependencies name exactly
the current completed Work members beneath the same item coordinate. That seal
also closes nested static groups. It does not close an unfinished sibling or the
parent expansion. Work totals exclude Control and retries; provider envelopes
remain mechanical native state, never domain acceptance or failure decoding.

Two optional payload-free InputHash properties preserve the original DTO
constructor ABI. SQLite fills them with SHA-256 hex identities. Older providers
that omit them remain conservative about per-item closure. No schema migration,
occurrence identity, step key, scheduling, authority or retry behavior changes.
The sealed occurrence set is prepared once in the frozen snapshot using shared
indexes; per-query seal lookup is constant time. Cache budgets include the new
retained data. The occurrence dictionary used during preparation is not retained.

Native preview.9 and final Fuwen preview.18 local qualification passes. The
[ZG-7 record](qualification/activity-groups/zg7.md) identifies exact source,
archives, regression receipts and passing Windows/Linux CI gates. Preview.17 remains
an intermediate checkpoint; preview.18 adds the structural body-work admission
correction found during the second pass. Earlier static, repeat and fan-out records retain their original pins.
