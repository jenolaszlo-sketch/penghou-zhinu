# Operator walkthrough

This walkthrough uses a temporary database and no credentials. It diagnoses a
blocked signal wait, supplies the signal, inspects the completed result, and
previews interventions, all through the `zhinu` CLI. Payloads stay hidden
unless `--include-payloads` is passed.

This walkthrough is about ordinary signals, not protected authorization approval.
Opening a version-5 database through the new SQLite package initializes and
upgrades it to schema 6 even for subsequent inspection commands. Stop older
workers and back up the database first; see [upgrade instructions](releasing.md).

```powershell
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs list
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs show <run-id>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs why-waiting <run-id>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs signal <run-id> release --data '"approved"'
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs events <run-id>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs restart-preview <run-id> approve
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs retention-preview --older-than-days 7
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs external-ops list <run-id>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs external-ops show <operation-id>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs restart <run-id> <step> --operation-id <guid>
dotnet run --project src/Penghou.Zhinu.Cli -- --db C:\Temp\walkthrough.db runs wait <run-id> [--timeout-seconds 300]
```

`external-ops list <run-id> [--status Status] [--limit N]` shows a run's
durable external-operation handles oldest first (step, attempt, provider,
status, recovery intent); `show` renders one handle with its correlation
payload, error, lease generation, and timestamps. These are the records a
durable activity registers before any external effect, so a stuck `Running`
handle, a `Cancelled` handle with its neutral reason (for example an
authority revocation), or a `Failed` handle with its exit code is visible
without opening SQLite directly.

Two durable rules follow from this command. Operational evidence is part of
the product surface: when durable execution creates external-operation
records, operators need a supported way to inspect them. And observability
must not require authority expansion: read-only inspection through the
existing repository APIs is preferable to adding engine or storage
capabilities, and correlation payloads stay redacted unless
`--include-payloads` is passed explicitly.

Remediation follows the same doctrine. `runs restart` executes a
`restart-preview` plan through the idempotent receipt API: `--operation-id`
is mandatory, so repeating identical intent returns the original receipt
instead of applying the restart twice, while a conflicting reuse is
rejected. The run returns to `Pending` for a worker to resume; the CLI
itself never executes workflow work. `runs wait` blocks until the run
reaches a terminal state (or the bounded timeout elapses) so scripts and
operators stop polling `show` by hand; terminal runs report immediately.

`fork-preview` has no executing counterpart yet, deliberately. Executing a
fork requires the same idempotent receipt semantics `restart` enjoys, and
the store has no idempotent fork operation: repeating a fork with the same
destination run ID fails instead of returning the original fork, and the CLI
will not synthesize receipt guarantees the store does not provide. Until
that store primitive exists, forking stays a plan-then-manual path; this is
a recorded capability boundary, not a missing flag.

`why-waiting` reports parked waits with their kind, status, signal, deadline,
and availability, plus whether the run currently has runnable work. Only
`runs signal` mutates state, through the normal audited signal API; every
other command reads. Add `--format json` for machine-readable output.

Authorization approval checkpoints are separate from these ordinary waits.
The CLI does not configure an authorizer, expose approval wakes, or inspect the
protected checkpoint repository. Its `why-waiting` output therefore does not
prove that a protected run is authorized or runnable. Use the trusted host's
`IWorkflowAuthorizationRepository.GetPendingAuthorizationAsync` and exact wake
protocol for protected approvals; sending an ordinary signal is not an approval
grant. See [authorization](workflow-authorization.md).
