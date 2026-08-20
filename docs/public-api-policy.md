# Public API policy

The `0.1.0-preview` API is treated as frozen after this checkpoint. Additive API
changes are permitted. Renames, removals, changed defaults, and semantic changes
to durable state transitions require a new preview minor and release notes.

CI builds, tests, formats, and packs every project for .NET 8 and .NET 10.
NuGet package validation is enabled for packable projects. Before the first
stable release, the package compatibility baseline will be changed from the
latest preview package to the selected release candidate.
