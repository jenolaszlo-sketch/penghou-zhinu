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

CI now repeats the declared-framework tests, isolated consumer and old-binary
probe on Windows and Ubuntu, retaining TRX, packages and consumer logs. Remote
CI results for these changes are still pending commit/push; the local proof is
Windows qualification, not a claim that the new remote jobs already passed.

Next commit and push the reviewed candidate, then let the user publish from the
existing main workflow. Verify the public artifacts before starting Hufu HA-1/2/3.
Keep [the canonical activity queue](../authority-extension-activities.md) and
[the runtime boundary](../workflow-authorization.md) as the implementation
handoff. Final concrete resource checks remain provider-owned; this callback
preflight does not create an atomic transaction with external effects.
