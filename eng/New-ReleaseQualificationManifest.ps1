#Requires -Version 7.0
# ZG-RQ-02 RG-01: Stage A release-qualification manifest generator.
#
# Runs inside the qualification workflow (ci.yml) after build, format, test,
# pack, package-closure and legacy-binary probes have all succeeded on the
# exact candidate commit. It freezes a machine-readable manifest binding the
# qualification run, the source commit and the SHA-256 of every produced
# package/symbol archive.
#
# The manifest is convenience metadata only: publication authority comes from
# GitHub's own run records, which the publisher re-verifies through the
# authenticated API in eng/Verify-QualificationRun.ps1 before trusting this
# manifest's run claims. Schema (release-qualification-manifest v1):
#   schemaVersion               integer, must be 1
#   profileId                   qualification profile identifier
#   repository                  owner/name of the qualifying repository
#   commitSha                   40-char lowercase hex source commit
#   workflowPath                repository path of the qualification workflow
#   runId                       qualification workflow run id (integer)
#   runUrl                      qualification run URL
#   conclusion                  qualification conclusion; "success" here
#   qualifiedAtUtc              ISO-8601 UTC timestamp
#   packageVersion              candidate package version
#   packages[]                  exactly the seven release packages, each:
#     id, version, nupkg, nupkgSha256, snupkg, snupkgSha256
#   targetFrameworks[]          target frameworks of the core package
#   dependencyConstraints{}     recorded dependency pins of the core package
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ArtifactsDirectory,
    [Parameter(Mandatory = $true)][string] $OutputPath,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $CommitSha = $env:GITHUB_SHA,
    [string] $WorkflowPath = '.github/workflows/ci.yml',
    [string] $RunId = $env:GITHUB_RUN_ID,
    [string] $RunUrl,
    [string] $Conclusion = 'success',
    [string] $ProfileId = 'zhinu-release-qualification-v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RunUrl) {
    $RunUrl = if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) {
        "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
    } else { '' }
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

if ([string]::IsNullOrWhiteSpace($Repository)) { throw 'Repository is required.' }
if ([string]::IsNullOrWhiteSpace($CommitSha)) { throw 'CommitSha is required.' }
if ($CommitSha -notmatch '^[0-9a-f]{40}$') { throw "CommitSha must be a 40-character lowercase commit SHA: $CommitSha" }
if ([string]::IsNullOrWhiteSpace($RunId)) { throw 'RunId is required.' }

$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$nupkgs = @(Get-ChildItem -LiteralPath $artifacts -Filter '*.nupkg' -File)
$snupkgs = @(Get-ChildItem -LiteralPath $artifacts -Filter '*.snupkg' -File)
if ($nupkgs.Count -ne 7) { throw "Expected exactly 7 nupkg files, found $($nupkgs.Count)." }
if ($snupkgs.Count -ne 7) { throw "Expected exactly 7 snupkg files, found $($snupkgs.Count)." }

$versions = @($nupkgs | ForEach-Object {
    if ($_.Name -match '^(.+)\.(\d+\.\d+\.\d.+)\.nupkg$') { $Matches[2] } else { $null }
} | Where-Object { $_ } | Select-Object -Unique)
if ($versions.Count -ne 1) { throw "Could not derive a single package version from artifacts: $($versions -join ', ')" }
$version = $versions[0]

$packages = @()
foreach ($id in $releasePackageIds) {
    $nupkg = $nupkgs | Where-Object { $_.Name -eq "$id.$version.nupkg" }
    if (-not $nupkg) { throw "Required release package missing: $id.$version.nupkg" }
    $snupkg = $snupkgs | Where-Object { $_.Name -eq "$id.$version.snupkg" }
    if (-not $snupkg) { throw "Required symbol package missing: $id.$version.snupkg" }
    $packages += [ordered]@{
        id            = $id
        version       = $version
        nupkg         = $nupkg[0].Name
        nupkgSha256   = (Get-FileHash -LiteralPath $nupkg[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        snupkg        = $snupkg[0].Name
        snupkgSha256  = (Get-FileHash -LiteralPath $snupkg[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$coreEntry = [System.IO.Compression.ZipFile]::OpenRead(
    (Join-Path $artifacts "Penghou.Zhinu.$version.nupkg"))
try {
    $nuspecEntry = $coreEntry.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    $nuspec = [xml]$reader.ReadToEnd()
    $reader.Close()
}
finally { $coreEntry.Dispose() }

$targetFrameworks = @(
    $nuspec.package.metadata.dependencies.group | ForEach-Object { $_.targetFramework })
$dependencyConstraints = @{}
foreach ($group in $nuspec.package.metadata.dependencies.group) {
    foreach ($dependency in $group.dependency) {
        if ($dependency.id -notin $dependencyConstraints.Keys) {
            $dependencyConstraints[$dependency.id] = $dependency.version
        }
    }
}

$manifest = [ordered]@{
    schemaVersion         = 1
    profileId             = $ProfileId
    repository            = $Repository
    commitSha             = $CommitSha.ToLowerInvariant()
    workflowPath          = $WorkflowPath
    runAttempt            = $(if ($env:GITHUB_RUN_ATTEMPT) { [int]$env:GITHUB_RUN_ATTEMPT } else { 1 })
    evidence              = @('build (ubuntu-latest)', 'build (windows-latest)')
    runId                 = [long]$RunId
    runUrl                = $RunUrl
    conclusion            = $Conclusion
    qualifiedAtUtc        = [DateTimeOffset]::UtcNow.ToString('o')
    packageVersion        = $version
    packages              = $packages
    targetFrameworks      = $targetFrameworks
    dependencyConstraints = $dependencyConstraints
}

$outputDirectory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($outputDirectory)) { $outputDirectory = (Get-Location).Path }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath
Write-Output "Release qualification manifest written: $OutputPath"
Write-Output "Commit $CommitSha, version $version, $($packages.Count) packages."

