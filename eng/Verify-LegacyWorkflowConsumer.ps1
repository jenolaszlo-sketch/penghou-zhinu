[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $PackageVersion,

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$expectedLegacyVersion = '0.1.0-preview.15'
$neutralPackageId = 'Penghou.Workflow.Abstractions'
$neutralPackageVersion = '0.1.0-preview.2'
$corePackageId = 'Penghou.Zhinu'
$sqlitePackageId = 'Penghou.Zhinu.Sqlite'
$resolvedArtifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$coreArtifact = Join-Path $resolvedArtifacts "$corePackageId.$PackageVersion.nupkg"
$sqliteArtifact = Join-Path $resolvedArtifacts "$sqlitePackageId.$PackageVersion.nupkg"
foreach ($artifact in @($coreArtifact, $sqliteArtifact)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Required candidate artifact is missing: $artifact"
    }
}

$runId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "penghou-legacy-workflow-consumer-$runId"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$runDirectory = Join-Path $outputRoot "legacy-consumer-$runId"
$consumerDirectory = Join-Path $runDirectory 'consumer'
$cacheDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "pzl-$runId"
$logsDirectory = Join-Path $runDirectory 'logs'
New-Item -ItemType Directory -Path $consumerDirectory, $cacheDirectory, $logsDirectory -Force | Out-Null

function ConvertTo-NormalizedPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('/', '\'))
}

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $LogPath,
        [Parameter(Mandatory = $false)][int[]] $AllowedExitCodes = @(0),
        [Parameter(Mandatory = $false)][switch] $ExpectFailure
    )

    Push-Location $consumerDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($ExpectFailure -and $exitCode -eq 0) {
        throw "$FilePath $($Arguments -join ' ') unexpectedly succeeded; expected a nonzero exit code. See '$LogPath'."
    }
    if (-not $ExpectFailure -and $AllowedExitCodes -notcontains $exitCode) {
        throw "$FilePath $($Arguments -join ' ') returned unexpected exit code $exitCode; allowed: $($AllowedExitCodes -join ', '). See '$LogPath'.`n$($output -join [Environment]::NewLine)"
    }
    return [ordered]@{
        ExitCode = $exitCode
        Output = @($output | ForEach-Object { $_.ToString() })
        Log = [System.IO.Path]::GetFileName($LogPath)
    }
}

function Get-NuspecDocument {
    param([Parameter(Mandatory = $true)][string] $PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1) {
            throw "Expected exactly one nuspec in '$PackagePath'; found $($entries.Count)."
        }
        $stream = $entries[0].Open()
        try {
            $document = [System.Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($stream)
            return $document
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-NupkgRuntimeAssembly {
    param(
        [Parameter(Mandatory = $true)][string] $PackagePath,
        [Parameter(Mandatory = $true)][string] $Framework,
        [Parameter(Mandatory = $true)][string] $AssemblyName
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entryName = "lib/$Framework/$AssemblyName"
        $entry = $archive.GetEntry($entryName)
        if ($null -eq $entry) {
            throw "Package '$PackagePath' does not contain the target assembly '$entryName'."
        }
        $destination = Join-Path $runDirectory "$([System.IO.Path]::GetFileNameWithoutExtension($PackagePath))-$Framework-$AssemblyName"
        $entryStream = $entry.Open()
        try {
            $outputStream = [System.IO.File]::Create($destination)
            try { $entryStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose() }
        }
        finally { $entryStream.Dispose() }
        return $destination
    }
    finally { $archive.Dispose() }
}

function Assert-PackageIdentity {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedId,
        [Parameter(Mandatory = $true)][string] $ExpectedVersion
    )

    $document = Get-NuspecDocument $Path
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw "Package '$Path' has no nuspec metadata." }
    $actualId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $actualVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if ($actualId -cne $ExpectedId -or $actualVersion -cne $ExpectedVersion) {
        throw "Package identity mismatch in '$Path': found '$actualId' '$actualVersion'."
    }
    if ($document.OuterXml -match '(?i)Penghou\.Hufu') {
        throw "Forbidden Hufu dependency/reference found in '$Path'."
    }
    return $document
}

$coreNuspec = Assert-PackageIdentity -Path $coreArtifact -ExpectedId $corePackageId -ExpectedVersion $PackageVersion
$sqliteNuspec = Assert-PackageIdentity -Path $sqliteArtifact -ExpectedId $sqlitePackageId -ExpectedVersion $PackageVersion
$coreDependencies = @($coreNuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']//*[local-name()='dependency']") |
    Where-Object { $_.GetAttribute('id') -ceq $neutralPackageId })
if ($coreDependencies.Count -eq 0 -or
    @($coreDependencies | Where-Object { $_.GetAttribute('version') -cne "[$neutralPackageVersion]" }).Count -gt 0) {
    throw "Candidate core nuspec must pin $neutralPackageId exactly as [$neutralPackageVersion]."
}
$sqliteCoreDependencies = @($sqliteNuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']//*[local-name()='dependency']") |
    Where-Object { $_.GetAttribute('id') -ceq $corePackageId })
if ($sqliteCoreDependencies.Count -eq 0 -or
    @($sqliteCoreDependencies | Where-Object { $_.GetAttribute('version').Trim('[', ']') -cne $PackageVersion }).Count -gt 0) {
    throw "Candidate SQLite nuspec must depend on $corePackageId $PackageVersion."
}

$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <config>
    <add key="globalPackagesFolder" value="$([System.Security.SecurityElement]::Escape($cacheDirectory))" />
  </config>
</configuration>
"@
$nugetConfigPath = Join-Path $runDirectory 'NuGet.Config'
Set-Content -LiteralPath $nugetConfigPath -Value $nugetConfig -Encoding utf8

$projectFile = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$corePackageId" Version="[$expectedLegacyVersion]" />
    <PackageReference Include="$sqlitePackageId" Version="[$expectedLegacyVersion]" />
    <PackageReference Include="$neutralPackageId" Version="[$neutralPackageVersion]" />
  </ItemGroup>
</Project>
"@
$projectPath = Join-Path $consumerDirectory 'LegacyConsumer.csproj'
Set-Content -LiteralPath $projectPath -Value $projectFile -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
if (Select-String -LiteralPath $projectPath -Pattern '<ProjectReference\b' -Quiet) {
    throw 'The legacy package consumer must not contain a ProjectReference.'
}

$program = @'
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using System.Text.Json;

var databasePath = Path.GetFullPath(args.Length > 0 ? args[0] : "legacy-workflow.db");
if (args.Length > 1 && args[1] == "--inspect")
{
    var inspect = await InspectAsync(databasePath);
    Console.WriteLine("LEGACY_QUALIFICATION_STATE=" + JsonSerializer.Serialize(inspect));
    return;
}

var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
var registry = new WorkflowRegistry()
    .Register("legacy-direct", "1", new LegacyWorkflow())
    .Register("legacy-builder", "1", new LegacyWorkflow());

await using (var directEngine = new WorkflowEngine(store, registry, new ZhinuOptions()))
{
    var output = await directEngine.RunAsync<string, string>("legacy-direct", "1", "direct");
    if (output != "legacy:direct") throw new InvalidOperationException("Direct constructor workflow returned unexpected output.");
}

await using (var builderEngine = new WorkflowEngineBuilder()
    .WithStore(store)
    .WithRegistry(registry)
    .WithOptions(new ZhinuOptions())
    .Build())
{
    var output = await builderEngine.RunAsync<string, string>("legacy-builder", "1", "builder");
    if (output != "legacy:builder") throw new InvalidOperationException("Builder workflow returned unexpected output.");
}

var state = await InspectAsync(databasePath);
Console.WriteLine("LEGACY_QUALIFICATION_STATE=" + JsonSerializer.Serialize(state));

static async Task<object> InspectAsync(string databasePath)
{
    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    await using var versionCommand = connection.CreateCommand();
    versionCommand.CommandText = "SELECT version FROM zhinu_schema WHERE id = 1;";
    var schemaVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());
    await using var countCommand = connection.CreateCommand();
    countCommand.CommandText = "SELECT COUNT(*) FROM workflow_steps;";
    var retainedStepRows = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
    await using var rowsCommand = connection.CreateCommand();
    rowsCommand.CommandText = "SELECT id, workflow_run_id, step_key, status, attempt, input_json, output_json, error_json, created_at FROM workflow_steps ORDER BY id;";
    await using var reader = await rowsCommand.ExecuteReaderAsync();
    var stepRows = new List<object>();
    while (await reader.ReadAsync())
    {
        stepRows.Add(new
        {
            Id = reader.GetString(0),
            RunId = reader.GetString(1),
            StepKey = reader.GetString(2),
            Status = reader.GetInt32(3),
            Attempt = reader.GetInt32(4),
            InputJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            OutputJson = reader.IsDBNull(6) ? null : reader.GetString(6),
            ErrorJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = reader.GetString(8)
        });
    }
    return new { SchemaVersion = schemaVersion, RetainedStepRows = retainedStepRows, StepRows = stepRows };
}

sealed class LegacyWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
        context.StepAsync("legacy-step", input, (value, _) => Task.FromResult($"legacy:{value}"),
            cancellationToken: cancellationToken);
}
'@
$programPath = Join-Path $consumerDirectory 'Program.cs'
Set-Content -LiteralPath $programPath -Value $program -Encoding utf8

$restoreResult = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @(
    'restore', $projectPath,
    '--configfile', $nugetConfigPath,
    '-p:NuGetAudit=false',
    "-p:RestorePackagesPath=$cacheDirectory"
) -LogPath (Join-Path $logsDirectory 'restore.log')

$assetsPath = Join-Path $consumerDirectory 'obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "Restore did not produce '$assetsPath'."
}
$assetsJson = Get-Content -LiteralPath $assetsPath -Raw
if ($assetsJson -match '(?i)Penghou\.Hufu') { throw 'Forbidden Hufu dependency/path found in project.assets.json.' }
$assets = $assetsJson | ConvertFrom-Json
$libraryKeys = @($assets.libraries.PSObject.Properties.Name)
foreach ($expectedLibrary in @("$corePackageId/$expectedLegacyVersion", "$sqlitePackageId/$expectedLegacyVersion", "$neutralPackageId/$neutralPackageVersion")) {
    if (-not (@($libraryKeys | Where-Object { $_ -ceq $expectedLibrary }).Count)) {
        throw "Expected resolved package '$expectedLibrary' is missing."
    }
}
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { [System.IO.Path]::GetFullPath($_) })
if ($packageFolders.Count -ne 1 -or
    (ConvertTo-NormalizedPath $packageFolders[0]) -ine (ConvertTo-NormalizedPath $cacheDirectory)) {
    throw "Restore did not use only the fresh public NuGet cache '$cacheDirectory'."
}

$targetResults = [System.Collections.Generic.List[object]]::new()
foreach ($framework in @('net8.0', 'net10.0')) {
    $build = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @(
        'build', $projectPath,
        '--configuration', 'Release',
        '--framework', $framework,
        '--no-restore',
        "-p:RestorePackagesPath=$cacheDirectory"
    ) -LogPath (Join-Path $logsDirectory "build-$framework.log")

    $outputDirectory = Join-Path $consumerDirectory "bin/Release/$framework"
    $applicationPath = Join-Path $outputDirectory 'LegacyConsumer.dll'
    $databasePath = Join-Path $runDirectory "legacy-$framework.db"
    $nugetCoreDll = Join-Path $cacheDirectory "$($corePackageId.ToLowerInvariant())/$expectedLegacyVersion/lib/$framework/Penghou.Zhinu.dll"
    $nugetSqliteDll = Join-Path $cacheDirectory "$($sqlitePackageId.ToLowerInvariant())/$expectedLegacyVersion/lib/$framework/Penghou.Zhinu.Sqlite.dll"
    $neutralDll = Join-Path $outputDirectory "$neutralPackageId.dll"
    foreach ($requiredPath in @($applicationPath, $nugetCoreDll, $nugetSqliteDll, $neutralDll)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required compiled/public package file is missing for ${framework}: $requiredPath"
        }
    }
    $compiledHash = (Get-FileHash -LiteralPath $applicationPath -Algorithm SHA256).Hash
    $neutralHashBeforeSwap = (Get-FileHash -LiteralPath $neutralDll -Algorithm SHA256).Hash
    $oldCoreHash = (Get-FileHash -LiteralPath $nugetCoreDll -Algorithm SHA256).Hash
    $oldSqliteHash = (Get-FileHash -LiteralPath $nugetSqliteDll -Algorithm SHA256).Hash

    $oldRun = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($applicationPath, $databasePath) `
        -LogPath (Join-Path $logsDirectory "old-$framework.log")
    $oldStateLine = @($oldRun.Output | Where-Object { $_.StartsWith('LEGACY_QUALIFICATION_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
    if (-not $oldStateLine) { throw "Legacy $framework execution emitted no database state." }
    $oldState = ($oldStateLine.Substring('LEGACY_QUALIFICATION_STATE='.Length)) | ConvertFrom-Json
    if ($oldState.SchemaVersion -ne 5 -or $oldState.RetainedStepRows -lt 2) {
        throw "Published runtime did not create a schema-5 database with retained rows on ${framework}: $($oldStateLine)"
    }

    $candidateCoreDll = Get-NupkgRuntimeAssembly -PackagePath $coreArtifact -Framework $framework -AssemblyName 'Penghou.Zhinu.dll'
    $candidateSqliteDll = Get-NupkgRuntimeAssembly -PackagePath $sqliteArtifact -Framework $framework -AssemblyName 'Penghou.Zhinu.Sqlite.dll'
    $appCoreDll = Join-Path $outputDirectory 'Penghou.Zhinu.dll'
    $appSqliteDll = Join-Path $outputDirectory 'Penghou.Zhinu.Sqlite.dll'
    Copy-Item -LiteralPath $candidateCoreDll -Destination $appCoreDll -Force
    Copy-Item -LiteralPath $candidateSqliteDll -Destination $appSqliteDll -Force

    $candidateRun = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($applicationPath, $databasePath) `
        -LogPath (Join-Path $logsDirectory "candidate-$framework.log")
    $candidateStateLine = @($candidateRun.Output | Where-Object { $_.StartsWith('LEGACY_QUALIFICATION_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
    if (-not $candidateStateLine) { throw "Candidate $framework execution emitted no database state." }
    $candidateState = ($candidateStateLine.Substring('LEGACY_QUALIFICATION_STATE='.Length)) | ConvertFrom-Json
    if ($candidateState.SchemaVersion -ne 6 -or $candidateState.RetainedStepRows -lt ($oldState.RetainedStepRows + 2)) {
        throw "Candidate runtime did not migrate schema 5 to 6 and retain the prior step rows on ${framework}: $($candidateStateLine)"
    }
    $migratedRowsById = @{}
    foreach ($row in @($candidateState.StepRows)) { $migratedRowsById[$row.Id] = $row }
    foreach ($oldRow in @($oldState.StepRows)) {
        if (-not $migratedRowsById.ContainsKey($oldRow.Id)) {
            throw "Candidate migration removed prior step row '$($oldRow.Id)' on $framework."
        }
        $migratedRowJson = ConvertTo-Json -InputObject $migratedRowsById[$oldRow.Id] -Depth 8 -Compress
        $oldRowJson = ConvertTo-Json -InputObject $oldRow -Depth 8 -Compress
        if ($migratedRowJson -cne $oldRowJson) {
            throw "Candidate migration changed persisted fields for prior step row '$($oldRow.Id)' on $framework."
        }
    }
    if ((Get-FileHash -LiteralPath $applicationPath -Algorithm SHA256).Hash -cne $compiledHash) {
        throw "Compiled consumer changed while swapping runtime assemblies on $framework."
    }
    if ((Get-FileHash -LiteralPath $neutralDll -Algorithm SHA256).Hash -cne $neutralHashBeforeSwap) {
        throw "Neutral workflow contract assembly changed during the runtime swap on $framework."
    }

    # NuGet's global cache lowercases package IDs in both directories and archive names.
    # Restore only the original published binaries from the fresh public-feed package cache.
    $originalCoreNupkg = Join-Path $cacheDirectory "$($corePackageId.ToLowerInvariant())/$expectedLegacyVersion/$($corePackageId.ToLowerInvariant()).$expectedLegacyVersion.nupkg"
    $originalSqliteNupkg = Join-Path $cacheDirectory "$($sqlitePackageId.ToLowerInvariant())/$expectedLegacyVersion/$($sqlitePackageId.ToLowerInvariant()).$expectedLegacyVersion.nupkg"
    $originalCoreDll = Get-NupkgRuntimeAssembly -PackagePath $originalCoreNupkg -Framework $framework -AssemblyName 'Penghou.Zhinu.dll'
    $originalSqliteDll = Get-NupkgRuntimeAssembly -PackagePath $originalSqliteNupkg -Framework $framework -AssemblyName 'Penghou.Zhinu.Sqlite.dll'
    Copy-Item -LiteralPath $originalCoreDll -Destination $appCoreDll -Force
    Copy-Item -LiteralPath $originalSqliteDll -Destination $appSqliteDll -Force

    if ((Get-FileHash -LiteralPath $appCoreDll -Algorithm SHA256).Hash -cne $oldCoreHash -or
        (Get-FileHash -LiteralPath $appSqliteDll -Algorithm SHA256).Hash -cne $oldSqliteHash) {
        throw "Restored published runtime assemblies differ from the original binaries on $framework."
    }
    if ((Get-FileHash -LiteralPath $applicationPath -Algorithm SHA256).Hash -cne $compiledHash) {
        throw "Compiled consumer changed while restoring published assemblies on $framework."
    }
    $oldWorker = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($applicationPath, $databasePath) `
        -LogPath (Join-Path $logsDirectory "old-worker-on-schema6-$framework.log") -ExpectFailure
    $oldWorkerText = $oldWorker.Output -join [Environment]::NewLine
    if ($oldWorker.ExitCode -eq 0 -or
        $oldWorkerText -notmatch '(?i)(ZhinuSchemaCompatibilityException|schema.*(6|newer|incompatible)|incompatible.*schema)') {
        throw "Published $framework worker did not fail clearly on the schema-6 database. See '$($oldWorker.Log)'."
    }
    $inspect = Invoke-NativeChecked -FilePath 'dotnet' -Arguments @($applicationPath, $databasePath, '--inspect') `
        -LogPath (Join-Path $logsDirectory "inspect-after-old-worker-$framework.log")
    $inspectLine = @($inspect.Output | Where-Object { $_.StartsWith('LEGACY_QUALIFICATION_STATE=', [System.StringComparison]::Ordinal) }) | Select-Object -Last 1
    $finalState = ($inspectLine.Substring('LEGACY_QUALIFICATION_STATE='.Length)) | ConvertFrom-Json
    if ($finalState.SchemaVersion -ne 6 -or $finalState.RetainedStepRows -ne $candidateState.RetainedStepRows) {
        throw "Rejected published worker changed the schema-6 database on $framework."
    }

    $targetResults.Add([ordered]@{
        TargetFramework = $framework
        PublishedRun = [ordered]@{
            ExitCode = $oldRun.ExitCode
            SchemaVersion = $oldState.SchemaVersion
            RetainedStepRows = $oldState.RetainedStepRows
            CoreAssemblyHash = $oldCoreHash
            SqliteAssemblyHash = $oldSqliteHash
        }
        CandidateRuntimeRun = [ordered]@{
            ExitCode = $candidateRun.ExitCode
            SchemaVersion = $candidateState.SchemaVersion
            RetainedStepRows = $candidateState.RetainedStepRows
        }
        CompiledConsumerSha256 = $compiledHash
        ContractAssemblySha256 = $neutralHashBeforeSwap
        OldWorkerOnNewSchema = [ordered]@{
            ExitCode = $oldWorker.ExitCode
            RejectedAsIncompatible = $true
            SchemaVersionAfterRejection = $finalState.SchemaVersion
            RetainedStepRowsAfterRejection = $finalState.RetainedStepRows
        }
    })
}

$qualification = [ordered]@{
    SchemaVersion = 1
    PackageVersion = $PackageVersion
    NeutralPackage = [ordered]@{ Id = $neutralPackageId; ExactVersion = $neutralPackageVersion }
    CandidateArtifacts = @(
        [ordered]@{ Id = $corePackageId; File = [System.IO.Path]::GetFileName($coreArtifact) },
        [ordered]@{ Id = $sqlitePackageId; File = [System.IO.Path]::GetFileName($sqliteArtifact) }
    )
    PublicRestore = [ordered]@{
        PackageSources = @('https://api.nuget.org/v3/index.json')
        FreshPackageCache = $cacheDirectory
        AssetsFile = [System.IO.Path]::GetRelativePath($runDirectory, $assetsPath)
        ResolvedPackages = @($libraryKeys)
        ForbiddenHufuDependencyScan = 'passed'
        ProjectReferences = 0
    }
    TargetFrameworkResults = $targetResults.ToArray()
    Status = 'passed'
}
$qualificationPath = Join-Path $runDirectory 'qualification.json'
$qualification | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $qualificationPath -Encoding utf8
Write-Output $qualificationPath
