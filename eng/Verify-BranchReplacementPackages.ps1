[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CandidateArtifactsDirectory,
    [Parameter(Mandatory)][string] $BaselineArtifactsDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z.+-]*$')][string] $CandidateVersion = '0.2.0-preview.10',
    [ValidateSet('0.2.0-preview.9')][string] $BaselineVersion = '0.2.0-preview.9'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$candidateFeed = (Resolve-Path -LiteralPath $CandidateArtifactsDirectory).Path
$baselineFeed = (Resolve-Path -LiteralPath $BaselineArtifactsDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$packageHashes = @()
foreach ($entry in @(@($candidateFeed, $CandidateVersion), @($baselineFeed, $BaselineVersion))) {
    foreach ($id in @('Penghou.Zhinu', 'Penghou.Zhinu.Sqlite')) {
        $package = Join-Path $entry[0] "$id.$($entry[1]).nupkg"
        if (-not (Test-Path -LiteralPath $package)) { throw "Missing package: $package" }
        $packageHashes += [ordered]@{ Id = $id; Version = $entry[1]; File = $package; Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}
$configPath = Join-Path $outputRoot 'nuget.config'
$candidateXml = [Security.SecurityElement]::Escape($candidateFeed)
$baselineXml = [Security.SecurityElement]::Escape($baselineFeed)
@"
<configuration><packageSources><clear/><add key="candidate" value="$candidateXml"/><add key="baseline" value="$baselineXml"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><clear/></packageSourceMapping></configuration>
"@ | Set-Content -LiteralPath $configPath -Encoding utf8

function Invoke-ProbeCommand([string[]] $Arguments, [string] $Log) {
    & dotnet @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE); see $Log" }
}

$candidateSource = @'
using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = args[0], Pooling = false, EnableWal = false });
var manifest = ActivityGroupManifest.Create([new ActivityGroupDefinition("branch", ActivityGroupKind.Logical, "Branch")]);
WorkflowRegistry Registry(string choice) => new WorkflowRegistry().Register(new WorkflowRegistration<string,string>(
    new WorkflowDefinition { Name = "branch-packages", Version = "1" }, () => new Branch(choice), manifest));
Guid id;
Guid oldStepId;
await using (var engine = new WorkflowEngine(store, Registry("a")))
{
    if (args.Length > 1) id = Guid.Parse(args[1]);
    else
    {
        id = await engine.StartAsync("branch-packages", "1", "input");
        await engine.ExecuteAsync(id);
        if (await engine.WaitForCompletionAsync<string>(id) != "a") throw new Exception("Initial branch failed.");
    }
    oldStepId = (await engine.GetStepsAsync(id)).Single(step => step.StepKey == "branch.a.a").Id;
    if (args.Length > 2 && oldStepId != Guid.Parse(args[2])) throw new Exception("Schema migration changed the original step identity.");
    await engine.RestartStepAsync(id, "selection", new RestartStepOptions { Mode = StepRestartMode.StepOnly });
}
await using (var engine = new WorkflowEngine(store, Registry("b")))
{
    await engine.ExecuteAsync(id);
    if (await engine.WaitForCompletionAsync<string>(id) != "b") throw new Exception("Replacement failed.");
    if ((await engine.GetStepsAsync(id)).Single(step => step.Id == oldStepId).Status != StepStatus.Completed) throw new Exception("History changed.");
}
using var reader = new WorkflowGroupReader(store);
var snapshot = (await reader.GetRunGroupSnapshotAsync(id)).Value!;
var expansions = (await reader.QueryGroupExpansionsAsync(id, snapshot.SnapshotToken)).Value!.Items;
var old = expansions.Single(e => e.FanOutKey == "branch.a");
var current = expansions.Single(e => e.FanOutKey == "branch.b");
var history = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, old.Items.Single().OccurrenceId)).Value!.State;
var state = (await reader.GetGroupStateAsync(id, snapshot.SnapshotToken, current.Items.Single().OccurrenceId)).Value!.State;
if (old.ReplacedByExpansionId != current.ExpansionId || old.IsCurrent || !current.IsComplete || history.Work.Known != 0
    || history.Reason != WorkflowGroupStateReason.HistoricalSelection || state.TotalCount != 1 || state.Status != StepStatus.Completed)
    throw new Exception("Packaged replacement projection failed.");
Console.WriteLine(JsonSerializer.Serialize(new { Schema = ZhinuSqliteSchema.CurrentVersion, Run = id, HistoryKnown = history.Work.Known,
    CurrentTotal = state.TotalCount, Expansions = expansions.Count, OldStepId = oldStepId }));

sealed class Branch(string choice) : IWorkflow<string,string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
    {
        var selected = await context.StepAsync("selection", input, (_, _) => Task.FromResult(choice), cancellationToken: ct);
        return (await context.FanOutAsync("branch." + selected, new[] { selected }, item => item,
            (item, _, _) => Task.FromResult(item), new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
            {
                DependsOn = ["selection"], FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("branch"), "selection")
                { ReplacesFanOutKey = selected == "b" ? "branch.a" : null }
            }, ct)).Single();
    }
}
'@
$baselineSource = @'
using System.Text.Json;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = args[0], Pooling = false, EnableWal = false });
if (args.Length > 1 && args[1] == "seed")
{
    var manifest = ActivityGroupManifest.Create([new ActivityGroupDefinition("branch", ActivityGroupKind.Logical, "Branch")]);
    var registry = new WorkflowRegistry().Register(new WorkflowRegistration<string,string>(
        new WorkflowDefinition { Name = "branch-packages", Version = "1" }, () => new OriginalBranch(), manifest));
    await using var engine = new WorkflowEngine(store, registry);
    var id = await engine.StartAsync("branch-packages", "1", "input");
    await engine.ExecuteAsync(id);
    if (await engine.WaitForCompletionAsync<string>(id) != "a") throw new Exception("Baseline execution failed.");
    var step = (await engine.GetStepsAsync(id)).Single(step => step.StepKey == "branch.a.a");
    Console.WriteLine(JsonSerializer.Serialize(new { Schema = ZhinuSqliteSchema.CurrentVersion, Run = id, OldStepId = step.Id }));
    return;
}
try { await store.InitializeAsync(); throw new Exception("Old worker accepted a replacement-bearing database."); }
catch (ZhinuSchemaCompatibilityException error) when (error.ExpectedVersion == 9 && error.DatabaseVersion == 10)
{ Console.WriteLine("SCHEMA_9_WORKER_REJECTED_SCHEMA_10"); }

sealed class OriginalBranch : IWorkflow<string,string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken ct)
    {
        var selected = await context.StepAsync("selection", input, (_, _) => Task.FromResult("a"), cancellationToken: ct);
        return (await context.FanOutAsync("branch." + selected, new[] { selected }, item => item,
            (item, _, _) => Task.FromResult(item), new FanOutOptions { MaxDegreeOfParallelism = 1 }, new StepOptions
            {
                DependsOn = ["selection"], FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("branch"), "selection")
            }, ct)).Single();
    }
}
'@

foreach ($kind in @('candidate', 'baseline')) {
    $projectRoot = Join-Path $outputRoot $kind
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    '<Project />' | Set-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Encoding utf8
    '<Project />' | Set-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.targets') -Encoding utf8
    $version = if ($kind -eq 'candidate') { $CandidateVersion } else { $BaselineVersion }
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><IsPackable>false</IsPackable><TargetFrameworks>net8.0;net10.0</TargetFrameworks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion></PropertyGroup><ItemGroup><PackageReference Include="Penghou.Zhinu" Version="[$version]"/><PackageReference Include="Penghou.Zhinu.Sqlite" Version="[$version]"/></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $projectRoot 'Probe.csproj') -Encoding utf8
    $source = if ($kind -eq 'candidate') { $candidateSource } else { $baselineSource }
    $source | Set-Content -LiteralPath (Join-Path $projectRoot 'Program.cs') -Encoding utf8
    $project = Join-Path $projectRoot 'Probe.csproj'
    $cache = Join-Path $projectRoot 'cache'
    Invoke-ProbeCommand @('restore', $project, '--configfile', $configPath, '--packages', $cache, '--force', '--no-cache') (Join-Path $projectRoot 'restore.log')
    Invoke-ProbeCommand @('build', $project, '-c', 'Release', '--no-restore') (Join-Path $projectRoot 'build.log')
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') -Raw | ConvertFrom-Json
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) { throw 'Probe resolved source projects.' }
    foreach ($package in @($packageHashes | Where-Object { $_.Version -eq $version })) {
        $cached = Join-Path $cache "$($package.Id.ToLowerInvariant())/$version/$($package.Id.ToLowerInvariant()).$version.nupkg"
        if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.Sha256) { throw "Cached package mismatch: $cached" }
    }
}
$frameworks = @()
foreach ($framework in @('net8.0', 'net10.0')) {
    $databasePath = Join-Path $outputRoot "$framework.db"
    if (Test-Path -LiteralPath $databasePath) { throw "Refusing to overwrite an existing probe database: $databasePath" }
    $seedLog = Join-Path $outputRoot "baseline-seed-$framework.log"
    Invoke-ProbeCommand @((Join-Path $outputRoot "baseline/bin/Release/$framework/Probe.dll"), $databasePath, 'seed') $seedLog
    $seed = Get-Content -LiteralPath $seedLog -Raw | ConvertFrom-Json
    if ($seed.Schema -ne 9) { throw 'Baseline worker did not create schema 9.' }
    $candidateLog = Join-Path $outputRoot "candidate-$framework.log"
    Invoke-ProbeCommand @((Join-Path $outputRoot "candidate/bin/Release/$framework/Probe.dll"), $databasePath, $seed.Run, $seed.OldStepId) $candidateLog
    $beforeHash = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $baselineLog = Join-Path $outputRoot "baseline-$framework.log"
    Invoke-ProbeCommand @((Join-Path $outputRoot "baseline/bin/Release/$framework/Probe.dll"), $databasePath) $baselineLog
    if ((Get-Content -LiteralPath $baselineLog -Raw) -notmatch 'SCHEMA_9_WORKER_REJECTED_SCHEMA_10') { throw 'Old worker did not reject schema 10.' }
    $afterHash = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($beforeHash -cne $afterHash) { throw 'Old worker changed the database bytes.' }
    $frameworks += [ordered]@{ Framework = $framework; BaselineSeed = $seed; Candidate = (Get-Content -LiteralPath $candidateLog -Raw | ConvertFrom-Json); OldWorkerRejected = $true; DatabaseUnchanged = $true; DatabaseSha256 = $beforeHash }
}
[ordered]@{ Status = 'passed'; PackageSha256 = $packageHashes; Frameworks = $frameworks; NoProjectReferences = $true } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputRoot 'qualification.json') -Encoding utf8
Write-Output "Branch replacement package qualification passed on both frameworks: $outputRoot"
