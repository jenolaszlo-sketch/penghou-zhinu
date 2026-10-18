[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $EvidenceDirectory,
    [string] $SupplementalResultsDirectory,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $RepositoryRoot 'docs/qualification/activity-groups' }
if (-not $SupplementalResultsDirectory) { $SupplementalResultsDirectory = Join-Path $RepositoryRoot 'artifacts/zg3b6-composition-tests' }
if (-not $OutputPath) { $OutputPath = Join-Path $RepositoryRoot 'artifacts/zg3b6-audit.json' }

function Read-GitBytes([string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $RepositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $buffer = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($buffer)
            $errorText = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "Git audit failed: $errorText" }
            return ,$buffer.ToArray()
        } finally { $buffer.Dispose() }
    } finally { $process.Dispose() }
}

function Read-GitText([string[]] $Arguments) {
    return [Text.Encoding]::UTF8.GetString((Read-GitBytes $Arguments)).Trim()
}

function Get-BytesHash([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-FileHash([string] $Path, [string] $Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required local artifact missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $Expected.ToLowerInvariant()) { throw "Artifact hash mismatch: $Path" }
}

function Assert-SourceHash([string] $Commit, $Entry) {
    if ($Entry.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $Entry.Path -match '(^/|\\|(^|/)\.\.(/|$)|:)') {
        throw 'Invalid source hash entry.'
    }
    $bytes = Read-GitBytes @('show', "${Commit}:$($Entry.Path)")
    $expected = $Entry.Sha256.ToLowerInvariant()
    if ((Get-BytesHash $bytes) -ceq $expected) { return 'git-blob' }
    # Historical Windows ledgers hash checkout bytes. Git normalizes text to LF;
    # accept only the corresponding CRLF checkout, without trimming or rewriting.
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $checkout = [Text.Encoding]::UTF8.GetBytes($text.Replace("`r`n", "`n").Replace("`n", "`r`n"))
    if ((Get-BytesHash $checkout) -ceq $expected) { return 'crlf-checkout' }
    throw "Source hash mismatch at $Commit $($Entry.Path)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$rows = [Collections.Generic.List[object]]::new()
$latestCases = [Collections.Generic.List[object]]::new()
$expectedTotals = @(1441, 1499, 1531, 1571, 1639)
$expectedProcess = @(8, 14, 14, 16, 16)
$historicalWorkers = @(16, 28, 28, 36, 38)
$retainedSourceCount = 0
foreach ($number in 1..5) {
    $path = Join-Path $EvidenceDirectory "zg3b$number.json"
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 80
    $stage = "ZG-3b.$number"
    if ($record.Stage -cne $stage -or $record.Status -cne 'passed' -or $record.PersistedSchemaVersion -ne 9 -or
        $record.RuntimeImplementationCommit -cne $record.PackageSourceCommit -or
        $record.TestTotal -ne $expectedTotals[$number - 1] -or $record.PackageVersion -cne '0.2.0-preview.4' -or
        (@($record.TargetFrameworks | Sort-Object) -join ',') -cne 'net10.0,net8.0') { throw "Invalid profile header: $stage" }
    $source = $record.RuntimeImplementationCommit
    if ($source -notmatch '^[0-9a-f]{40}$') { throw "Invalid source commit: $stage" }
    $null = Read-GitBytes @('merge-base', '--is-ancestor', $source, 'HEAD')
    $sourceHashes = @($record.SourceSha256)
    if ($sourceHashes.Count -eq 0 -or @($sourceHashes.Path | Select-Object -Unique).Count -ne $sourceHashes.Count) {
        throw "Missing or duplicate source entries: $stage"
    }
    $sourceChecks = @($sourceHashes | ForEach-Object {
        [ordered]@{ Path = $_.Path; Sha256 = $_.Sha256; Encoding = (Assert-SourceHash $source $_) }
    })
    foreach ($name in @('RetainedBaselineFilesSha256','RetainedRuntimeSha256')) {
        if ($record.PSObject.Properties[$name]) {
            foreach ($entry in $record.$name) { $null = Assert-SourceHash $source $entry; $retainedSourceCount++ }
        }
    }
    $total = 0; $testChecks = @()
    foreach ($test in $record.Tests) {
        $testPath = Join-Path $RepositoryRoot $test.File
        Assert-FileHash $testPath $test.Sha256
        [xml]$trx = Get-Content -LiteralPath $testPath -Raw
        $c = $trx.TestRun.ResultSummary.Counters
        if ([int]$c.total -ne $test.Total -or [int]$c.passed -ne $test.Total -or [int]$c.failed -ne 0 -or
            [int]$c.notExecuted -ne 0 -or $test.Passed -ne $test.Total -or $test.Failed -ne 0 -or $test.NotExecuted -ne 0) {
            throw "Test receipt mismatch: $stage $($test.File)"
        }
        $total += $test.Total
        $testChecks += [ordered]@{ File = $test.File; Sha256 = $test.Sha256; Passed = $test.Total }
        if ($number -eq 5) {
            $framework = if ($test.File -match '_net8\.0') { 'net8.0' } else { 'net10.0' }
            foreach ($case in $trx.TestRun.Results.UnitTestResult) {
                $latestCases.Add([pscustomobject]@{ Framework = $framework; Name = [string]$case.testName; Outcome = [string]$case.outcome })
            }
        }
    }
    if ($total -ne $record.TestTotal -or @($record.Tests).Count -ne 11 -or $record.TestAssembliesAndFrameworks -ne 11) {
        throw "Test totals mismatch: $stage"
    }
    $receipts = @($record.ProcessRecoveryReceipts)
    if ($receipts.Count -ne $expectedProcess[$number - 1]) { throw "Process receipt count mismatch: $stage" }
    foreach ($framework in @('net8.0','net10.0')) {
        if (@($receipts | Where-Object TargetFramework -CEQ $framework).Count -ne $receipts.Count / 2) {
            throw "Missing process framework: $stage $framework"
        }
    }
    if (($number -eq 3 -and $record.FreshProcessStartsForNewCases -ne 28) -or
        ($number -eq 4 -and ($record.FreshProcessStarts -ne 36 -or $record.NegativeOmissionProcessCases -ne 2)) -or
        ($number -eq 5 -and ($record.FreshProcessStarts -ne 38 -or
            [int]($receipts | ForEach-Object { $_.Receipt.FreshProcessStarts } | Measure-Object -Sum).Sum -ne 38))) {
        throw "Historical worker count mismatch: $stage"
    }
    if ($record.PackageClosure.Status -cne 'passed' -or $record.LegacyBinary.Status -cne 'passed' -or
        $record.LegacyBinary.ExpectedCandidateSchemaVersion -ne 9 -or @($record.PackageSha256).Count -ne 7) {
        throw "Compatibility gate mismatch: $stage"
    }
    $packageChecks = @()
    # Resolve artifacts within this checkout, not the historical machine's path.
    $packages = Join-Path $RepositoryRoot "artifacts/zg3b$number-packages"
    foreach ($package in $record.PackageSha256) {
        $packagePath = Join-Path $packages $package.File
        Assert-FileHash $packagePath $package.Sha256
        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $entries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
            if ($entries.Count -ne 1) { throw "Ambiguous package metadata: $packagePath" }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ([string]$spec.package.metadata.repository.commit -cne $source) { throw "Package source mismatch: $packagePath" }
        } finally { $archive.Dispose() }
        $packageChecks += [ordered]@{ File = $package.File; Sha256 = $package.Sha256; RepositoryCommit = $source }
    }
    foreach ($framework in @('net8.0','net10.0')) {
        $closure = @($record.PackageClosure.TargetFrameworkResults | Where-Object TargetFramework -CEQ $framework)
        $legacy = @($record.LegacyBinary.TargetFrameworkResults | Where-Object TargetFramework -CEQ $framework)
        if ($closure.Count -ne 1 -or $closure[0].Result -cne 'passed' -or $legacy.Count -ne 1 -or
            $legacy[0].CandidateRuntimeRun.ExitCode -ne 0 -or $legacy[0].CandidateRuntimeRun.SchemaVersion -ne 9 -or
            $legacy[0].OldWorkerOnNewSchema.RejectedAsIncompatible -ne $true -or
            $legacy[0].OldWorkerOnNewSchema.SchemaVersionAfterRejection -ne 9 -or
            $legacy[0].OldWorkerOnNewSchema.RetainedStepRowsAfterRejection -ne 4) { throw "Framework compatibility mismatch: $stage $framework" }
    }
    $rows.Add([ordered]@{
        Stage = $stage; Evidence = "docs/qualification/activity-groups/zg3b$number.json"
        EvidenceSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        RuntimeSource = $source; Schema = 9; FullSuiteTests = $total
        PositiveProcessReceipts = $receipts.Count; NegativeOmissionCases = $(if ($number -eq 4) { 2 } else { 0 })
        HistoricalNewWorkerStarts = $historicalWorkers[$number - 1]
        SourceChecks = $sourceChecks; TestChecks = $testChecks; PackageChecks = $packageChecks
        Compatibility = 'Two framework package consumers, legacy candidate substitution, schema migration and old-worker rejection passed in the retained qualification.'
    })
}

$latest = $rows[$rows.Count - 1].RuntimeSource
$null = Read-GitBytes @('diff', '--exit-code', $latest, '--', 'src', 'eng/Verify-WorkflowPackageClosure.ps1', 'eng/Verify-LegacyWorkflowConsumer.ps1')
$supplementalSource = 'tests/Penghou.Zhinu.Sqlite.Tests/ActivityGroupCompositionBoundaryTests.cs'
$changedTests = Read-GitText @('diff', '--name-only', $latest, '--', 'tests')
$untracked = Read-GitText @('ls-files', '--others', '--exclude-standard', '--', 'src', 'tests')
foreach ($entry in @(($changedTests + "`n" + $untracked) -split '\r?\n' | Where-Object { $_ })) {
    if ($entry -cne $supplementalSource) { throw "Unexpected runtime/test change invalidates evidence reuse: $entry" }
}

$guardSpecs = @(
    @{ Name = 'Direct Group on fan-out'; Prefix = 'Penghou.Zhinu.Sqlite.Tests.ActivityGroupBoundaryTests.GroupedFanOutOverloads_RejectBeforeAnyItemCallback'; Minimum = 4 },
    @{ Name = 'Explicit loop parent on grouped fan-out'; Prefix = 'Penghou.Zhinu.Tests.ActivityGroupNestedScopeTests.GroupedFanOutUnderNestedLoopParent_RejectsBeforeItemExecution'; Minimum = 2 },
    @{ Name = 'Competing typed loop/fan-out coordinate'; Prefix = 'Penghou.Zhinu.Tests.ActivityGroupNestedScopeTests.NestedLoopAndFanOutCoordinate_CannotBeCombinedIntoCompetingScope'; Minimum = 1 },
    @{ Name = 'Store grouped fan-out/fork rejection'; Prefix = 'Penghou.Zhinu.Sqlite.Tests.ActivityGroupForkStoreTests.GroupedFork_RejectsFanOutEvidenceAndExistingDestinationWithoutChangingSource'; Minimum = 1 },
    @{ Name = 'Wrong-manifest and loop-coordinate child binding'; Prefix = 'Penghou.Zhinu.Sqlite.Tests.ActivityGroupChildTests.SameRunWrongManifestAndUntypedLoopScopeFailBeforeChildBoundaryClaims'; Minimum = 2 },
    @{ Name = 'Missing grouped fan-out capability'; Prefix = 'Penghou.Zhinu.Sqlite.Tests.ActivityGroupFanOutEngineTests.MissingFanOutCapability_FailsBeforeSelectorAndOnMarkedResume'; Minimum = 1 }
)
$guardChecks = @()
foreach ($guard in $guardSpecs) {
    foreach ($framework in @('net8.0','net10.0')) {
        $cases = @($latestCases | Where-Object { $_.Framework -ceq $framework -and $_.Name.StartsWith($guard.Prefix, [StringComparison]::Ordinal) })
        if ($cases.Count -lt $guard.Minimum -or @($cases | Where-Object Outcome -CNE 'Passed').Count -gt 0) {
            throw "Missing retained rejection evidence: $($guard.Name) $framework"
        }
        $guardChecks += [ordered]@{ Guard = $guard.Name; Framework = $framework; Tests = @($cases.Name); Outcome = 'Passed' }
    }
}

$supplementalFiles = @(Get-ChildItem -LiteralPath $SupplementalResultsDirectory -Filter '*.trx')
if ($supplementalFiles.Count -ne 2) { throw 'Expected two fresh framework receipts for the consolidation coverage gaps.' }
$supplemental = @(); $supplementalReceipts = @(); $supplementalFrameworks = @()
foreach ($file in $supplementalFiles) {
    [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
    $framework = if ($file.Name -match '_net8\.0') { 'net8.0' } elseif ($file.Name -match '_net10\.0') { 'net10.0' } else { throw 'Unrecognized supplemental framework.' }
    $supplementalFrameworks += $framework
    $c = $trx.TestRun.ResultSummary.Counters
    if ([int]$c.total -ne 4 -or [int]$c.passed -ne 4 -or [int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0) {
        throw "Supplemental coverage did not pass: $framework"
    }
    $caseNames = @()
    foreach ($case in $trx.TestRun.Results.UnitTestResult) {
        if (-not ([string]$case.testName).StartsWith('Penghou.Zhinu.Sqlite.Tests.ActivityGroupCompositionBoundaryTests.', [StringComparison]::Ordinal) -or
            $case.outcome -cne 'Passed') { throw 'Unexpected supplemental test case.' }
        $caseNames += [string]$case.testName
        foreach ($line in ([string]$case.Output.StdOut -split '\r?\n')) {
            if ($line -match '^COMPOSITION_REJECTION_EVIDENCE=(.*)$') {
                $supplementalReceipts += [ordered]@{ Framework = $framework; Test = [string]$case.testName; Receipt = ($Matches[1] | ConvertFrom-Json -Depth 50) }
            }
        }
    }
    $supplemental += [ordered]@{
        File = [IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName).Replace('\','/')
        Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Framework = $framework; Passed = 4; Cases = $caseNames
    }
}
if ((@($supplementalFrameworks | Sort-Object -Unique) -join ',') -cne 'net10.0,net8.0' -or $supplementalReceipts.Count -ne 8) {
    throw 'Incomplete supplemental rejection receipts.'
}

$audit = [ordered]@{
    SchemaVersion = 1; Stage = 'ZG-3b.6'; Status = 'passed'; AuditedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    AuditedHead = (Read-GitText @('rev-parse','HEAD')); RuntimePackageSource = $latest
    Scope = 'Consolidation of retained exact-source profile evidence plus focused new composition rejection tests; no new runtime code or process-loss executions.'
    RuntimeAndTestTreeMatch = 'Runtime and both consumer probes match the latest full-suite/package source. Existing tests are unchanged; only the supplemental composition test file is added.'
    LatestFullSuiteTests = 1639; ProfileRecords = $rows.ToArray()
    VerifiedLocalTestFiles = 55; VerifiedLocalPackages = 35
    VerifiedSourceHashEntries = [int]($rows | ForEach-Object { @($_.SourceChecks).Count } | Measure-Object -Sum).Sum
    VerifiedRetainedSourceHashEntries = $retainedSourceCount
    PositiveHistoricalProcessReceipts = 68; NegativeHistoricalOmissionCases = 2; HistoricalTrancheWorkerStarts = 146
    WorkerCounting = 'Tranche-specific historical workers, including the child five-worker sequence. Not a count of new executions or every repeated worker in cumulative full-suite runs.'
    ConsolidationFinding = 'Nested repetition required no runtime algorithm, schema, or identity redesign.'
    RetainedGuardChecks = $guardChecks
    SupplementalSource = $supplementalSource
    SupplementalSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $RepositoryRoot $supplementalSource) -Algorithm SHA256).Hash.ToLowerInvariant()
    SupplementalTestExecutions = 8; SupplementalTests = $supplemental; SupplementalReceipts = $supplementalReceipts
    NewProcessLossWorkers = 0
}
$outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$audit | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output ([IO.Path]::GetFullPath($OutputPath))
