# ZG-RQ-02 RG-01 live-API negative evidence.
# Read-only: queries real GitHub-hosted historical runs and records refusals.
$ErrorActionPreference = 'Stop'
$pwsh = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\pwsh.exe'
$token = gh auth token
$repo = 'jenolaszlo-sketch/penghou-zhinu'
$verifier = 'C:\Users\Laszlos\source\repos\zg-rq-02-zhinu\eng\Verify-QualificationRun.ps1'
$root = 'C:\Windows\TEMP\opencode\zgrq02\live-api-negatives'
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Path $root | Out-Null

$cases = @(
    @{ Scenario = 'R02-no-runs'; Commit = 'd4968ea6214b520147b2bb1c9161a5ba7954c497'; Workflow = 'ci.yml'; Expected = 'REFUSED: no workflow runs at commit' },
    @{ Scenario = 'R02-old-format-no-stage-a-artifact'; Commit = 'f0edf1714013106bdbce090179342ccb029eaf83'; Workflow = 'ci.yml'; Expected = 'REFUSED: qualification artifact absent' },
    @{ Scenario = 'R04-failed-qualification'; Commit = '18c0624a823e6a8c38aae85b98cab1bc7e67a362'; Workflow = 'ci.yml'; Expected = 'REFUSED: latest qualification failed' },
    @{ Scenario = 'R05-cancelled-qualification'; Commit = 'ca0c466ff704e88b800a42a94405f4e27db76df1'; Workflow = 'ci.yml'; Expected = 'REFUSED: latest qualification cancelled' },
    @{ Scenario = 'R13-wrong-workflow-substitution'; Commit = '18c0624a823e6a8c38aae85b98cab1bc7e67a362'; Workflow = 'publish.yml'; Expected = 'REFUSED: no runs of expected qualification workflow' }
)

$results = @()
foreach ($case in $cases) {
    $outDir = Join-Path $root $case.Scenario
    $detail = ''
    $exitCode = 0
    try {
        $output = & $pwsh -NoProfile -File $verifier `
            -Repository $repo `
            -CommitSha $case.Commit `
            -GitHubToken $token `
            -WorkflowFile $case.Workflow `
            -ExpectedWorkflowPath ".github/workflows/$($case.Workflow)" `
            -OutputDirectory $outDir 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { $detail = $output.Trim() }
    }
    catch {
        $exitCode = 1
        $detail = $_.Exception.Message
    }
    $observed = if ($exitCode -eq 0) { 'ACCEPTED' } else { 'REFUSED' }
    $results += [ordered]@{
        scenario = $case.Scenario
        commit = $case.Commit
        workflow = $case.Workflow
        expected = $case.Expected
        observed = $observed
        exitCode = $exitCode
        detail = $detail
    }
}

$payload = [ordered]@{
    executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    repository = $repo
    kind = 'LIVE API (read-only) against real GitHub-hosted historical runs'
    note = 'Each case invokes the production eng/Verify-QualificationRun.ps1 against the authenticated GitHub API. No artifact is published; refusals occur before any download/publish side effect.'
    scenarios = $results
}
$json = $payload | ConvertTo-Json -Depth 5
$target = 'C:\Users\Laszlos\source\repos\zg-rq-02-zhinu\docs\evidence\zg-rq-02\local\live-api-negatives.json'
Set-Content -Path $target -Value $json -Encoding utf8
Write-Output $json
