# Neutral workflow package adoption record

This is the historical ZA-2 dependency-adoption checkpoint. Runtime authorization
was subsequently implemented and pushed in `e91804a`; current release status is
in [the authorization qualification](qualification/workflow-authorization.md)
and [release instructions](releasing.md). The 927-case figures below belong to
the earlier adoption proof, not the current 1,017-case runtime matrix.

## Published package evidence

- Package: `Penghou.Workflow.Abstractions` version `0.1.0-preview.2`.
- Penghou source revision: `5a76b7c`.
- Publishing run: [GitHub Actions run 37116694209](https://github.com/jenolaszlo-sketch/penghou/actions/runs/37116694209).
- Publishing and verification jobs: all four passed.
- Package provenance: the public NuGet package contents match the exact CI
  artifact, excluding the repository signature.
- Isolated consumer: restored from a fresh cache using only nuget.org, then
  built and ran on .NET 8 and .NET 10. Its only package dependency is
  `Penghou.Workflow.Abstractions`.

These facts close WA-1/2/3 and establish the neutral package's publication and
consumer behavior.

## Zhinu source adoption and qualification

The Zhinu candidate core project references the exact package version:

```xml
<PackageReference Include="Penghou.Workflow.Abstractions" Version="[0.1.0-preview.2]" />
```

Source adoption changes exactly three files: `src/Penghou.Zhinu/Penghou.Zhinu.csproj`
pins this package, and `src/Penghou.Zhinu.Agents/WorkflowContextAgentExtensions.cs`
and `tests/Penghou.Zhinu.Agents.Tests/WorkflowContextAgentExtensionsTests.cs`
use explicit namespace aliases. The
public `Microsoft.Agents.AI.Workflows.Workflow` signatures remain unchanged.
Qualification recorded 927 passing cases with zero failures or skips: 461 on .NET 8 and 466 on
.NET 10. Core, SQLite, Hosting, OpenTelemetry and Agents each target both
frameworks; ASP.NET Core's five cases target .NET 10 only. Exact TRX evidence is
in eleven `za2-final*` and `za2-agents*` files. The explicit format check passed
for both alias files.

| Acceptance item | Qualification evidence | Result |
| --- | --- | --- |
| Declared target-framework test suites | All declared Zhinu TFM suites against the exact package pin | .NET 8: 461 passed; .NET 10: 466 passed; 927 total, zero failures or skips. Core (49), SQLite (375), Hosting (23), OpenTelemetry (1) and Agents (13) each target both frameworks; ASP.NET Core adds five .NET 10-only cases. Eleven `za2-final*` and `za2-agents*` TRX files record the runs |
| Public API and namespace compatibility | API inventories, analyzer-enforced builds and alias format checks | Existing API inventories are unchanged; the alias names the same public type. The explicit format check passes for both alias files |
| Old binary compatibility | Existing compiled consumer against the candidate core assembly | Probe compiled against published Zhinu/Sqlite preview.15; candidate core ran direct construction, builder construction and `StepAsync` on both TFMs without recompiling the probe |
| Exact package graph and package closure | Fresh-GUID empty-cache package-only consumer over all seven local CI packages | Passed using `package-consumer/verify-package-consumer.ps1` with `0.1.0-preview.15-ci.wa3.20261003`: Workflow 2 and external dependencies restore only from public NuGet; build and run pass on .NET 8 and .NET 10. Both nuspec dependency groups contain the exact workflow contract dependency, and the resolved asset graph contains no Hufu dependency |

ZA-2 source adoption and package qualification are complete. Machine-readable
evidence is recorded in the
[workflow package adoption record](qualification/workflow-package-adoption.json).
At that checkpoint, ZA-3A/3B/4 remained separate runtime implementation gates;
they are now locally qualified and pushed. Package adoption itself did not
implement the Hufu adapter, Luban LW-1 or publish a Zhinu release. Preview.15
remains immutable; release `0.2.0-preview.1` separately after CI passes.

Adding the `Penghou.Workflow` namespace can make unqualified `Workflow` type
references ambiguous in consumers under `Penghou` that import another workflow
library. Use the fully qualified type or an explicit alias, as the Agents adapter
does. The old-binary probe covers direct construction, builder construction and
`StepAsync`; it is not an exhaustive test of every downstream application.
