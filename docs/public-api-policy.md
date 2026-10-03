# Public API policy

The `0.1.0-preview` API is treated as frozen after this checkpoint. Additive API
changes are permitted. Renames, removals, changed defaults, and semantic changes
to durable state transitions require a new preview minor and release notes.

CI builds, tests, formats and packs the declared project frameworks. Six public
packages target .NET 8 and .NET 10; ASP.NET Core targets .NET 10 only. Schema 6
and protected state transitions are introduced in `0.2.0-preview.1`, with
[release notes](releases/0.2.0-preview.1.md) and [upgrade instructions](releasing.md).
NuGet package validation is enabled for packable projects. Before the first
stable release, the package compatibility baseline will be changed from the
latest preview package to the selected release candidate.
