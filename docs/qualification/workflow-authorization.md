# Zhinu authorization candidate qualification

ZA-3A, ZA-3B and ZA-4 are locally qualified for `0.2.0-preview.1` on Windows
x64. [The machine-readable record](workflow-authorization.json) contains the
declared-framework test matrix, artifact hashes and linked consumer evidence.
ZA-6 publication remains open; these changes have not been published.

The Release build passes with analyzers enabled and zero warnings or errors;
format verification passes. All 1,017 tests pass: 506 on .NET 8 and 511 on .NET
10. The ASP.NET Core package targets .NET 10 only. The matrix includes ordinary
workflow regressions plus registration, denial, evidence failure, durable
approval, revocation, retry, compensation, declaration drift, generation/lease
fencing and recovery tests. The shipped public API inventories are unchanged;
additions and the schema-version constant change are recorded in unshipped API.

Seven package and symbol pairs were packed using a unique local qualification
version. DLL, XML documentation, README and PDB contents were inspected. An
isolated consumer restores those Zhinu artifacts with the exact published
`Penghou.Workflow.Abstractions` `0.1.0-preview.2` from nuget.org, using a fresh
cache, no project references and no Hufu dependencies. Real SQLite workflows
pass on both frameworks through direct construction and a protected builder.
See [package closure](workflow-authorization-package-closure.json).

A second consumer compiles once against published Zhinu/SQLite preview.15.
Only the two runtime assemblies are swapped for the candidate. The unchanged
application passes on both frameworks, upgrades its actual schema-5 database
to schema 6 and preserves the selected fields of every prior step row. Restoring
the old runtime assemblies then produces the expected incompatible-schema
failure. See [binary and migration evidence](workflow-authorization-legacy-consumer.json).

Implementation was committed and pushed in `e91804a`. The JSON and artifact
hashes above retain the original local proof taken before that commit.
CI repeats the tests and consumer probes on Windows and Ubuntu, retaining TRX,
packages and consumer logs. The first [remote run](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37132951810)
passed Ubuntu build, format, tests and pack, then failed because the generated
consumer inherited library public-API analyzer settings. The script now creates
its own empty build props/targets. The corrected consumer passes locally on
both frameworks with output nested beneath the repository, matching CI layout.
Windows CI also found that `TimelySignal_WinsDeadlineRace` depended on sending
a signal within 300 milliseconds of real time. The test now uses the existing
controllable clock: it persists the signal before the deadline, then resumes
after the deadline. The four signal-parking tests pass on both frameworks.
These corrections change qualification tooling and a test, not runtime behavior.
A successful rerun for the corrected release commit remains required.

The follow-up [run 37135986361](https://github.com/jenolaszlo-sketch/penghou-zhinu/actions/runs/37135986361)
passed the complete Windows job. Ubuntu passed build, format, tests, pack and
package closure, then exposed mixed-case archive filenames in the legacy
probe's NuGet cache lookup. Both lookup filenames now use the lowercase package
ID, matching the restored cache on case-sensitive filesystems. The corrected
legacy probe passes locally on .NET 8/10 with a fresh cache, unchanged consumer
binaries, schema-5-to-6 migration and old-worker rejection. Restored old DLLs
are also checked against their original hashes. An explicit filename-case check
rejects both prior paths and accepts both corrected paths. These checks are
local Windows evidence; the new Ubuntu CI run remains the Linux qualification.

After CI passes, the user publishes the seven-package release set from the
existing main workflow. See [release and upgrade instructions](../releasing.md).
Verify the public artifacts before starting Hufu HA-1/2/3.
Keep [the canonical activity queue](../authority-extension-activities.md) and
[the runtime boundary](../workflow-authorization.md) as the implementation
handoff. Final concrete resource checks remain provider-owned; this callback
preflight does not create an atomic transaction with external effects.
