#Requires -Version 7.0
# ZG-RQ-02 RG-01: Stage B part 2 - verify the downloaded qualification
# artifact set against the manifest and the accepted candidate identity.
# Pure offline validation: the run authority was established by
# eng/Verify-QualificationRun.ps1; this script refuses any deviation in the
# manifest schema (R12), source binding (R06), run binding (R14), package
# hashes (R07), package set (R08/R09), package identity or version (R10), or
# dependency constraints (R11). Every refusal happens before any publication
# step can consume the artifacts.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ManifestPath,
    [Parameter(Mandatory = $true)][string] $ArtifactsDirectory,
    [Parameter(Mandatory = $true)][string] $ExpectedRepository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommitSha,
    [Parameter(Mandatory = $true)][string] $ExpectedWorkflowPath,
    [Parameter(Mandatory = $true)][long] $ExpectedRunId,
    [Parameter(Mandatory = $true)][string] $ExpectedVersion,
    [string] $ExpectedNeutralDependency = '[0.1.0-preview.2]',
    [string] $NeutralDependencyId = 'Penghou.Workflow.Abstractions'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-PackageArchive {
    param([string]$Path, [string]$Id, [string]$Version, [string]$Commit)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $specs = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($specs.Count -ne 1) { throw "Expected one nuspec: $Path" }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml]$doc = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $doc.package.metadata
        if ($meta.id -cne $Id -or $meta.version -cne $Version -or $meta.repository.commit -cne $Commit) {
            throw "Archive identity/version/source mismatch: $Path"
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if (-not $seen.Add($entry.FullName) -or $entry.FullName -match '(^/|\\|(^|/)\.\.(/|$))') {
                throw "Unsafe or duplicate archive member: $Path"
            }
        }
    } finally { $zip.Dispose() }
}

$releasePackageIds = @(
    'Penghou.Zhinu',
    'Penghou.Zhinu.Agents',
    'Penghou.Zhinu.Hosting',
    'Penghou.Zhinu.Hosting.AspNetCore',
    'Penghou.Zhinu.OpenTelemetry',
    'Penghou.Zhinu.Sqlite',
    'Penghou.Zhinu.Testing'
)

function Assert-ManifestField {
    param([Parameter(Mandatory = $true)]$Object, [Parameter(Mandatory = $true)][string] $Name)
    if (-not ($Object.PSObject.Properties[$Name] -and $null -ne $Object.PSObject.Properties[$Name].Value)) {
        throw "Malformed manifest: required field '$Name' is missing (R12 REFUSE)."
    }
}

try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "Malformed manifest: not valid JSON (R12 REFUSE): $($_.Exception.Message)"
}
if (-not $manifest) { throw 'Malformed manifest: empty document (R12 REFUSE).' }

Assert-ManifestField $manifest 'schemaVersion'
if ([int]$manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schema version '$($manifest.schemaVersion)' (R12 REFUSE)."
}
foreach ($field in @('profileId', 'repository', 'commitSha', 'workflowPath',
                     'runId', 'conclusion', 'packageVersion', 'packages',
                     'targetFrameworks', 'dependencyConstraints')) {
    Assert-ManifestField $manifest $field
}
if ($manifest.profileId -cne 'zhinu-release-qualification-v1') { throw 'Unexpected qualification profile.' }
if ($manifest.conclusion -ne 'success') {
    throw "Manifest records conclusion '$($manifest.conclusion)' (R04 REFUSE)." }
if ($manifest.repository -ne $ExpectedRepository) {
    throw "Manifest repository '$($manifest.repository)' does not match '$ExpectedRepository' (R13 REFUSE)."
}
if (([string]$manifest.commitSha).ToLowerInvariant() -ne $ExpectedCommitSha) {
    throw "Manifest commit '$($manifest.commitSha)' is not the publication commit '$ExpectedCommitSha' (R06 REFUSE)."
}
if ($manifest.workflowPath -ne $ExpectedWorkflowPath) {
    throw "Manifest workflow path '$($manifest.workflowPath)' is not the expected qualification workflow (R13 REFUSE)."
}
if ([long]$manifest.runId -ne $ExpectedRunId) {
    throw "Manifest run id '$($manifest.runId)' is not the API-verified qualification run '$ExpectedRunId' (R14 REFUSE)."
}
if ($manifest.packageVersion -ne $ExpectedVersion) {
    throw "Manifest version '$($manifest.packageVersion)' does not match expected version '$ExpectedVersion' (R10 REFUSE)."
}

$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$packages = @($manifest.packages)
if ($packages.Count -ne 7) {
    throw "Manifest lists $($packages.Count) packages; exactly 7 are required (R12 REFUSE)."
}
$listedIds = @($packages | ForEach-Object { $_.id })
foreach ($id in $releasePackageIds) {
    if ($id -notin $listedIds) { throw "Manifest package set is missing '$id' (R08 REFUSE)." }
}
$extraIds = @($listedIds | Where-Object { $_ -notin $releasePackageIds })
if ($extraIds.Count -gt 0) {
    throw "Manifest contains unauthorized package ids: $($extraIds -join ', ') (R09 REFUSE)."
}

foreach ($package in $packages) {
    if ($package.version -ne $ExpectedVersion) {
        throw "Package '$($package.id)' version '$($package.version)' differs from '$ExpectedVersion' (R10 REFUSE)."
    }
    foreach ($fileField in @('nupkg', 'snupkg')) {
        Assert-ManifestField $package $fileField
        Assert-ManifestField $package "$fileField`Sha256"
        if ($package.$fileField -cne "$($package.id).$ExpectedVersion.$fileField") {
            throw "Noncanonical package filename: $($package.$fileField)"
        }
        $path = Join-Path $artifacts $package.$fileField
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Qualified file '$($package.$fileField)' is missing from the artifact set (R08 REFUSE)."
        }
        Assert-PackageArchive $path $package.id $ExpectedVersion $ExpectedCommitSha
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $listed = ([string]$package."$fileField`Sha256").ToLowerInvariant()
        if ($listed -notmatch '^[0-9a-f]{64}$') {
            throw "Malformed manifest: '$($package.id)' $fileField hash is not 64 hex characters (R12 REFUSE)."
        }
        if ($actual -ne $listed) {
            throw "Hash mismatch for '$($package.$fileField)': expected $listed, found $actual (R07 REFUSE)."
        }
    }
}

# No unlisted package files may appear in the set (R09).
$listedFiles = @($packages | ForEach-Object { $_.nupkg; $_.snupkg })
$presentFiles = @(Get-ChildItem -LiteralPath $artifacts -File |
    Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | ForEach-Object { $_.Name })
$unlisted = @($presentFiles | Where-Object { $_ -notin $listedFiles })
if ($unlisted.Count -gt 0) {
    throw "Unexpected unqualified files in the artifact set: $($unlisted -join ', ') (R09 REFUSE)."
}

# Dependency constraints: the accepted neutral-contract pin must be recorded
# and must match the packed core nuspec (R11).
$constraints = $manifest.dependencyConstraints
if ($constraints.PSObject.Properties[$NeutralDependencyId]) {
    $recorded = [string]$constraints.PSObject.Properties[$NeutralDependencyId].Value
} else {
    $recorded = $null
}
if ($recorded -ne $ExpectedNeutralDependency) {
    throw "Manifest dependency constraint for '$NeutralDependencyId' is '$recorded'; the accepted pin is '$ExpectedNeutralDependency' (R11 REFUSE)."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$coreArchive = [System.IO.Compression.ZipFile]::OpenRead(
    (Join-Path $artifacts "Penghou.Zhinu.$ExpectedVersion.nupkg"))
try {
    $nuspecEntry = $coreArchive.Entries |
        Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspec = [xml]$reader.ReadToEnd()
    $reader.Close()
}
finally { $coreArchive.Dispose() }
$actualVersions = @()
foreach ($group in $nuspec.package.metadata.dependencies.group) {
    foreach ($dependency in $group.dependency) {
        if ($dependency.id -eq $NeutralDependencyId) { $actualVersions += $dependency.version }
    }
}
$actualVersions = @($actualVersions | Select-Object -Unique)
if ($actualVersions.Count -ne 1 -or $actualVersions[0] -ne $ExpectedNeutralDependency) {
    throw "Core package declares '$NeutralDependencyId' versions '$($actualVersions -join ', ')'; the accepted pin is '$ExpectedNeutralDependency' (R11 REFUSE)."
}

$targetFrameworks = @($manifest.targetFrameworks | ForEach-Object { [string]$_ })
if ('net8.0' -notin $targetFrameworks -or 'net10.0' -notin $targetFrameworks) {
    throw "Manifest target frameworks do not include the required matrix: $($targetFrameworks -join ', ') (R12 REFUSE)."
}

Write-Output ("VERIFIED: 7 package/symbol pairs, commit $ExpectedCommitSha, " +
    "run $ExpectedRunId, version $ExpectedVersion, $NeutralDependencyId $ExpectedNeutralDependency.")
