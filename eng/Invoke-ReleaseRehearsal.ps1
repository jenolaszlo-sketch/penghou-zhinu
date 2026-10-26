#Requires -Version 7.0
# ZG-RQ-02 RG-01: local release rehearsal matrix (nonpublishing).
#
# Exercises the REAL offline verifier (eng/Verify-ReleaseArtifacts.ps1)
# against fixture copies of a qualified package set. Each scenario applies one
# deliberate fault to a fresh copy and asserts the verifier refuses BEFORE any
# publication operation could consume the artifacts. This harness contains no
# package-push code path at all; publication side effects exist only in
# publish.yml's final job behind verification and the protected environment.
#
# Scenario map (ZG-RQ-02 section 7.1, offline share):
#   R01  exact commit, successful qualification, matching artifacts -> PASS
#   R06  qualified commit differs from publication commit    -> REFUSE
#   R07  artifact SHA-256 differs                            -> REFUSE
#   R08  required package missing                            -> REFUSE
#   R09  unexpected package included                          -> REFUSE
#   R10  package version differs                              -> REFUSE
#   R11  dependency constraint differs                        -> REFUSE
#   R12  manifest malformed or incomplete                     -> REFUSE
#   R14  manifest run id differs from the verified run        -> REFUSE
#   R18  qualified artifact set valid                        -> RELEASE-ELIGIBLE, NO PUSH
# R02-R05 and R13 are exercised against the live GitHub API by the hosted
# rehearsal workflow; R15-R17 belong to the Fuwen and environment evidence.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $QualifiedArtifactsDirectory,
    [Parameter(Mandatory = $true)][string] $ManifestPath,
    [Parameter(Mandatory = $true)][string] $Repository,
    [Parameter(Mandatory = $true)][string] $CommitSha,
    [Parameter(Mandatory = $true)][long] $RunId,
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$verifier = Join-Path $PSScriptRoot 'Verify-ReleaseArtifacts.ps1'
$expectedWorkflow = '.github/workflows/ci.yml'
$neutralId = 'Penghou.Workflow.Abstractions'
$results = [System.Collections.Generic.List[object]]::new()

function New-Fixture {
    param([Parameter(Mandatory = $true)][string] $Name)
    $dir = Join-Path $OutputDirectory $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -Path (Join-Path $QualifiedArtifactsDirectory '*.nupkg') -Destination $dir
    Copy-Item -Path (Join-Path $QualifiedArtifactsDirectory '*.snupkg') -Destination $dir
    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $dir 'manifest.json')
    return $dir
}

function Edit-Manifest {
    param(
        [Parameter(Mandatory = $true)][string] $Directory,
        [Parameter(Mandatory = $true)][scriptblock] $Edit
    )
    $path = Join-Path $Directory 'manifest.json'
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    & $Edit $manifest
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][ValidateSet('PASS', 'REFUSE')][string] $Expectation,
        [Parameter(Mandatory = $true)][scriptblock] $Fault
    )
    $directory = New-Fixture $Id
    & $Fault $directory
    $output = ''
    $refused = $false
    try {
        $output = & $verifier `
            -ManifestPath (Join-Path $directory 'manifest.json') `
            -ArtifactsDirectory $directory `
            -ExpectedRepository $Repository `
            -ExpectedCommitSha $CommitSha `
            -ExpectedWorkflowPath $expectedWorkflow `
            -ExpectedRunId $RunId `
            -ExpectedVersion $Version 2>&1 | Out-String
    }
    catch {
        $refused = $true
        $output = $_.Exception.Message
    }
    $expected = if ($Expectation -eq 'REFUSE') { 'REFUSED' } else { 'PASS' }
    $observed = if ($refused) { 'REFUSED' } else { 'PASS' }
    $status = if ($observed -eq $expected) { 'PASS' } else { 'FAIL' }
    if ($Expectation -eq 'PASS' -and $status -eq 'PASS') {
        $status = 'RELEASE-ELIGIBLE (NO PUSH)'
    }
    $results.Add([pscustomobject]@{
        scenario = $Id
        expected = $expected
        observed = $observed
        status   = $status
        detail   = (@($output -split [Environment]::NewLine) | Where-Object { $_ } | Select-Object -Last 1)
    })
}

Invoke-Scenario 'R01' 'PASS' { param($d) }

Invoke-Scenario 'R06' 'REFUSE' {
    param($d)
    Edit-Manifest $d { param($m) $m.commitSha = '0123456789abcdef0123456789abcdef01234567' }
}

Invoke-Scenario 'R07' 'REFUSE' {
    param($d)
    $manifest = Get-Content -LiteralPath (Join-Path $d 'manifest.json') -Raw | ConvertFrom-Json
    $target = Join-Path $d $manifest.packages[0].nupkg
    $stream = [System.IO.File]::Open($target, [System.IO.FileMode]::Append)
    try { $stream.WriteByte(0) } finally { $stream.Close() }
}

Invoke-Scenario 'R08' 'REFUSE' {
    param($d)
    $manifest = Get-Content -LiteralPath (Join-Path $d 'manifest.json') -Raw | ConvertFrom-Json
    Remove-Item -LiteralPath (Join-Path $d $manifest.packages[3].nupkg)
}

Invoke-Scenario 'R09' 'REFUSE' {
    param($d)
    $manifest = Get-Content -LiteralPath (Join-Path $d 'manifest.json') -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath (Join-Path $d $manifest.packages[0].nupkg) `
        -Destination (Join-Path $d "Penghou.Zhinu.Unauthorized.$Version.nupkg")
}

Invoke-Scenario 'R10' 'REFUSE' {
    param($d)
    Edit-Manifest $d { param($m) $m.packageVersion = '0.2.0-preview.99' }
}

Invoke-Scenario 'R11' 'REFUSE' {
    param($d)
    Edit-Manifest $d { param($m) $m.dependencyConstraints.'Penghou.Workflow.Abstractions' = '[1.0.0]' }
}

Invoke-Scenario 'R12' 'REFUSE' {
    param($d)
    Set-Content -LiteralPath (Join-Path $d 'manifest.json') -Value '{ not valid json'
}

Invoke-Scenario 'R14' 'REFUSE' {
    param($d)
    Edit-Manifest $d { param($m) $m.runId = $RunId + 1 }
}

Invoke-Scenario 'R12-missing-manifest' 'REFUSE' { param($d) Remove-Item -LiteralPath (Join-Path $d 'manifest.json') }
Invoke-Scenario 'R07-incorrect-hash' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.packages[2].nupkgSha256 = '0' * 64 } }
Invoke-Scenario 'R04-failed-qualification' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.conclusion = 'failure' } }
Invoke-Scenario 'R13-wrong-repository' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.repository = 'unrelated/repository' } }
Invoke-Scenario 'R12-wrong-profile' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.profileId = 'unrelated' } }
Invoke-Scenario 'R10-package-identity' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.packages[2].id = 'Wrong.Package' } }
Invoke-Scenario 'R12-path-traversal' 'REFUSE' { param($d) Edit-Manifest $d { param($m) $m.packages[2].nupkg = '../escape.nupkg' } }

# R18: fresh valid fixture verifies; the harness structurally has no push.
$finalDirectory = New-Fixture 'R18'
$r18Refused = $false
$verifiedOutput = ''
try {
    $verifiedOutput = & $verifier `
        -ManifestPath (Join-Path $finalDirectory 'manifest.json') `
        -ArtifactsDirectory $finalDirectory `
        -ExpectedRepository $Repository `
        -ExpectedCommitSha $CommitSha `
        -ExpectedWorkflowPath $expectedWorkflow `
        -ExpectedRunId $RunId `
        -ExpectedVersion $Version 2>&1 | Out-String
}
catch {
    $r18Refused = $true
    $verifiedOutput = $_.Exception.Message
}
$results.Add([pscustomobject]@{
    scenario = 'R18'
    expected = 'RELEASE-ELIGIBLE (NO PUSH)'
    observed = if ($r18Refused) { 'REFUSED' } else { 'RELEASE-ELIGIBLE (NO PUSH)' }
    status   = if ($r18Refused) { 'FAIL' } else { 'RELEASE-ELIGIBLE (NO PUSH)' }
    detail   = 'Verifier accepted the qualified set; this harness has no publication code path'
})

$summary = [ordered]@{
    executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    scenarios     = $results
    noPushReason  = 'Publication side effects exist only in publish.yml:publish, which requires the successful verify job and the protected nuget environment approval; this harness and the rehearsal workflow contain no push steps.'
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'rehearsal-matrix.json')
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.status -like 'FAIL*' })
if ($failed.Count -gt 0) { throw "Rehearsal matrix failures: $($failed.Count)" }
Write-Output "Rehearsal matrix complete: $($results.Count) scenarios, all as expected."

