#Requires -Version 7.0
# ZG-RQ-02 RG-01: Stage B part 1 - verify the qualification run of the exact
# publication commit through GitHub's authenticated API and download its
# qualification artifacts. This script is the trust boundary: a manifest found
# in artifacts can never assert its own authority; only a completed,
# successful run of the expected qualification workflow in this repository,
# at the exact requested commit, with an allowed trigger event, yields the
# artifacts that later steps may verify and publish.
#
# Fail-closed on: commit unknown; no runs; only pending/in_progress runs
# (R03); only failed runs (R04); only cancelled/skipped runs (R05); no run of
# the expected workflow (R13: a different workflow with a similar name cannot
# substitute); PR-triggered runs are never trusted; head_sha mismatch (R06);
# missing or expired artifacts; manifest missing from the artifact set.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Repository,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string] $CommitSha,
    [Parameter(Mandatory = $true)][string] $GitHubToken,
    [string] $WorkflowFile = 'ci.yml',
    [string] $ExpectedWorkflowPath = '.github/workflows/ci.yml',
    [string] $ArtifactName = 'release-qualification-ubuntu-latest',
    [string] $ManifestName = 'release-qualification-manifest.json',
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string[]] $AllowedEvents = @('push', 'workflow_dispatch')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$base = "https://api.github.com/repos/$Repository"

function Invoke-GitHubApi {
    param([Parameter(Mandatory = $true)][string] $Uri)
    $response = Invoke-RestMethod -Uri $Uri -Headers @{
        Authorization = "Bearer $GitHubToken"
        Accept        = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    return $response
}

# Resolve the expected qualification workflow by file name. A commit cannot
# rename this away: the workflow must exist at its canonical path.
$workflow = $null
try {
    $workflow = Invoke-GitHubApi "$base/actions/workflows/$WorkflowFile"
}
catch {
    throw "The expected qualification workflow '$WorkflowFile' was not found in $Repository (R13 REFUSE): $($_.Exception.Message)"
}
if ($workflow.path -ne $ExpectedWorkflowPath) {
    throw "Workflow '$WorkflowFile' resolved to unexpected path '$($workflow.path)' (R13 REFUSE)."
}
$workflowId = [long]$workflow.id

# Enumerate runs for the exact commit (bounded pagination).
$runs = @()
for ($page = 1; $page -le 10; $page++) {
    $response = Invoke-GitHubApi "$base/actions/runs?head_sha=$CommitSha&per_page=100&page=$page"
    $runs += @($response.workflow_runs)
    if (@($response.workflow_runs).Count -lt 100) { break }
}
if ($runs.Count -eq 0) {
    throw "No workflow runs found for commit $CommitSha (R02 REFUSE: qualification absent)."
}

$workflowRuns = @($runs | Where-Object { [long]$_.workflow_id -eq $workflowId })
if ($workflowRuns.Count -eq 0) {
    throw "No runs of the expected qualification workflow at commit $CommitSha (R13 REFUSE). Found runs: $(@($runs | ForEach-Object { $_.name }) -join ', ')."
}

$qualified = @($workflowRuns | Where-Object {
    $_.status -eq 'completed' -and
    $_.conclusion -eq 'success' -and
    ($AllowedEvents -contains $_.event) -and
    ([string]$_.head_sha).ToLowerInvariant() -eq $CommitSha })
if ($qualified.Count -eq 0) {
    $detail = @($workflowRuns | ForEach-Object { "$($_.status)/$($_.conclusion)/$($_.event)" }) -join ', '
    throw "No successful qualification run for commit $CommitSha (REFUSE). Observed: $detail"
}
if (@($qualified | Where-Object { $_.event -eq 'pull_request' }).Count -gt 0) {
    throw 'Internal error: pull_request runs must never be accepted.'
}

# Never fall back to an older green run after a newer failed/pending run.
$run = @($workflowRuns | Where-Object { $_.event -in $AllowedEvents } | Sort-Object -Property run_number -Descending)[0]
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') { throw 'Latest qualification is not successful.' }
if ($run.path -ne $ExpectedWorkflowPath) { throw 'Run workflow path mismatch.' }
$jobs = Invoke-GitHubApi "$base/actions/runs/$($run.id)/attempts/$($run.run_attempt)/jobs?per_page=100"
foreach ($name in @('build (ubuntu-latest)', 'build (windows-latest)')) {
    $job = @($jobs.jobs | Where-Object { $_.name -eq $name })
    if ($job.Count -ne 1 -or $job[0].conclusion -ne 'success') { throw "Required qualification job absent or failed: $name" }
}
if ($run.head_repository.full_name -cne $Repository) {
    throw "Qualification run $($run.id) belongs to repository '$($run.head_repository.full_name)' (R13 REFUSE)."
}

# Download the qualification artifact set of that exact run.
$artifacts = Invoke-GitHubApi "$base/actions/runs/$($run.id)/artifacts?per_page=100"
$matches = @($artifacts.artifacts | Where-Object { $_.name -eq $ArtifactName })
if ($matches.Count -ne 1) { throw 'Expected exactly one qualification artifact.' }
$artifact = $matches[0]
if ($artifact.workflow_run.id -ne $run.id -or $artifact.workflow_run.head_sha -ne $CommitSha) { throw 'Artifact run/source mismatch.' }
if (-not $artifact) {
    throw "Qualification run $($run.id) has no '$ArtifactName' artifact (R02 REFUSE: no qualified artifact set)."
}
if ($artifact.expired) {
    throw "Qualification artifact '$ArtifactName' of run $($run.id) has expired (R02 REFUSE)."
}

if (Test-Path -LiteralPath $OutputDirectory) { throw 'Download destination must not already exist.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$zipPath = Join-Path $OutputDirectory "$ArtifactName.zip"
Invoke-WebRequest -Uri $artifact.archive_download_url -OutFile $zipPath -Headers @{
    Authorization = "Bearer $GitHubToken"
    Accept        = 'application/vnd.github+json'
}
Expand-Archive -LiteralPath $zipPath -DestinationPath $OutputDirectory -Force
Remove-Item -LiteralPath $zipPath -Force

$manifestPath = Join-Path $OutputDirectory $ManifestName
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Qualification artifact set of run $($run.id) is missing '$ManifestName' (R12 REFUSE)."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.runAttempt -ne $run.run_attempt -or $manifest.runId -ne $run.id -or $manifest.commitSha -ne $CommitSha) {
    throw 'Manifest is from a different run, attempt, or source.'
}
$result = [ordered]@{
    verifiedArtifactId = [long]$artifact.id
    verifiedRunAttempt = [int]$run.run_attempt
    verifiedRunId     = [long]$run.id
    verifiedCommitSha = ([string]$run.head_sha).ToLowerInvariant()
    verifiedWorkflow  = $run.path
    verifiedEvent     = $run.event
    manifestPath      = $manifestPath
    outputDirectory   = (Resolve-Path -LiteralPath $OutputDirectory).Path
}
Write-Output ($result | ConvertTo-Json)
