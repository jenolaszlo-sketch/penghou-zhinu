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
```

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
