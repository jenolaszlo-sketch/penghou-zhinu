#Requires -Version 7.0
param([string]$Version, [string]$CommitSha = $env:GITHUB_SHA,
      [string]$RefType = $env:GITHUB_REF_TYPE, [string]$RefName = $env:GITHUB_REF_NAME,
      [string]$EventName = $env:GITHUB_EVENT_NAME)
$ErrorActionPreference = 'Stop'
# Preserve the accepted manual-main path; its exact SHA is independently API-qualified.
if ($RefType -ceq 'branch' -and $RefName -ceq 'main' -and $EventName -ceq 'workflow_dispatch') { return }
if ($RefType -cne 'tag' -or $RefName -cne "v$Version") { throw 'Publication requires the exact checked-in version tag.' }
$resolved = (& git rev-parse --verify "refs/tags/$RefName^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $resolved -cne $CommitSha) { throw 'Release tag does not identify the qualified source commit.' }
