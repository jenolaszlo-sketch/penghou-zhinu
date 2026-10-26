#Requires -Version 7.0
# Offline API-shape fixtures exercise the actual downloader, never GitHub authorization.
param([string]$QualifiedArtifactsDirectory, [string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if (Test-Path $OutputDirectory) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory $OutputDirectory | Out-Null
$sourceManifest=Get-Content (Join-Path $QualifiedArtifactsDirectory 'release-qualification-manifest.json') -Raw | ConvertFrom-Json
$repo=$sourceManifest.repository; $sha=$sourceManifest.commitSha; $runId=[long]$sourceManifest.runId
$zip=Join-Path $OutputDirectory 'fixture.zip'
Compress-Archive -Path "$QualifiedArtifactsDirectory/*" -DestinationPath $zip
$global:zgFixtureScenario=''; $global:zgFixtureDownloads=0
function Invoke-RestMethod {
    param($Uri,$Headers)
    $run=[pscustomobject]@{ name='CI'; id=$runId; run_number=1; run_attempt=1; workflow_id=42; path='.github/workflows/ci.yml'; head_sha=$sha; status='completed'; conclusion='success'; event='push'; head_repository=@{full_name=$repo} }
    switch ($global:zgFixtureScenario) {
        'pending' { $run.status='in_progress'; $run.conclusion=$null }
        'failed' { $run.conclusion='failure' }
        'cancelled' { $run.conclusion='cancelled' }
        'unrelated-workflow' { $run.workflow_id=99 }
        'wrong-source' { $run.head_sha='0'*40 }
        'pull-request' { $run.event='pull_request' }
        'wrong-repository' { $run.head_repository.full_name='other/repository' }
        'stale-attempt' { $run.run_attempt=2 }
    }
    if ($Uri -match '/actions/workflows/') { return @{id=42;path='.github/workflows/ci.yml'} }
    if ($Uri -match '/attempts/.+/jobs') {
        return @{jobs=@(@{name='build (ubuntu-latest)';conclusion='success'},@{name='build (windows-latest)';conclusion=$(if($global:zgFixtureScenario -eq 'missing-matrix'){ 'skipped' }else{'success'})})}
    }
    if ($Uri -match '/artifacts\?') {
        $artifact=@{id=7; name='release-qualification-ubuntu-latest'; expired=($global:zgFixtureScenario -eq 'expired'); workflow_run=@{id=$runId;head_sha=$sha}; archive_download_url='https://fixture.invalid/artifact'}
        if ($global:zgFixtureScenario -eq 'unrelated-artifact-run') { $artifact.workflow_run.id=$runId+1 }
        if ($global:zgFixtureScenario -eq 'artifact-absent') { return @{artifacts=@()} }
        return @{artifacts=@($artifact)}
    }
    if ($Uri -match '/actions/runs\?') {
        if ($global:zgFixtureScenario -eq 'absent') { return @{workflow_runs=@()} }
        if ($global:zgFixtureScenario -eq 'newer-failure') {
            $later=$run.PSObject.Copy();$later.run_number=2;$later.conclusion='failure'
            return @{workflow_runs=@($run,$later)}
        }
        return @{workflow_runs=@($run)}
    }
    throw "Unexpected fixture endpoint: $Uri"
}
function Invoke-WebRequest { param($Uri,$OutFile,$Headers) $global:zgFixtureDownloads++; Copy-Item -LiteralPath $zip -Destination $OutFile }
$cases=[ordered]@{
    valid=''; absent='No workflow runs'; pending='No successful qualification'; failed='No successful qualification'; cancelled='No successful qualification';
    'unrelated-workflow'='No runs of the expected'; 'wrong-source'='No successful qualification'; 'pull-request'='No successful qualification';
    'wrong-repository'='belongs to repository'; 'stale-attempt'='different run, attempt'; 'missing-matrix'='Required qualification job';
    expired='expired'; 'unrelated-artifact-run'='Artifact run/source mismatch'; 'artifact-absent'='exactly one qualification artifact'; 'newer-failure'='Latest qualification'
}
$results=@()
foreach($case in $cases.Keys) {
    $global:zgFixtureScenario=$case; $global:zgFixtureDownloads=0; $message='';$refused=$false
    try { & "$PSScriptRoot/Verify-QualificationRun.ps1" -Repository $repo -CommitSha $sha -GitHubToken fixture-only -OutputDirectory (Join-Path $OutputDirectory $case) | Out-Null }
    catch { $message=$_.Exception.Message; $refused=$true }
    $pass=if($case -eq 'valid'){ -not $refused -and $global:zgFixtureDownloads -eq 1 }else{ $refused -and $message -match $cases[$case] }
    $results+=@{scenario=$case;result=$(if($pass){'PASS'}else{'FAIL'});detail=$message;downloads=$global:zgFixtureDownloads;kind='OFFLINE API FIXTURE'}
}
# Simulated git response: no real tag is created or changed.
function git { param([Parameter(ValueFromRemainingArguments)]$Arguments) $global:LASTEXITCODE=0; return $sha }
foreach($case in @('tag-positive','branch-denied','wrong-tag','wrong-tag-source')) {
    $message=''; $refused=$false
    try {
        & "$PSScriptRoot/Verify-ReleaseRef.ps1" -Version $sourceManifest.packageVersion `
            -CommitSha $(if($case -eq 'wrong-tag-source'){'0'*40}else{$sha}) `
            -RefType $(if($case -eq 'branch-denied'){'branch'}else{'tag'}) `
            -RefName $(if($case -eq 'wrong-tag'){'v0.0.0'}else{"v$($sourceManifest.packageVersion)"})
    } catch { $refused=$true; $message=$_.Exception.Message }
    $pass=if($case -eq 'tag-positive'){-not $refused}else{$refused -and $message -match 'Publication requires|Release tag does not identify'}
    $results+=@{scenario=$case;result=$(if($pass){'PASS'}else{'FAIL'});detail=$message;kind='OFFLINE REF FIXTURE'}
}
$results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'qualification-boundary.json')
$results | ForEach-Object { "$($_.scenario): $($_.result) $($_.detail)" }
if (@($results | Where-Object result -EQ 'FAIL').Count) { throw 'Boundary tests failed.' }
