# Operator walkthrough

This walkthrough uses a temporary database and no credentials. It diagnoses a
blocked signal wait, supplies the signal, inspects the completed result, and
previews interventions, all through the `zhinu` CLI. Payloads stay hidden
unless `--include-payloads` is passed.

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
