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

$packageIds = @(
    'Penghou.Zhinu',
    'Penghou.Zhinu.Agents',
    'Penghou.Zhinu.Hosting',
    'Penghou.Zhinu.Hosting.AspNetCore',
    'Penghou.Zhinu.OpenTelemetry',
    'Penghou.Zhinu.Sqlite',
    'Penghou.Zhinu.Testing'
)
$neutralPackageId = 'Penghou.Workflow.Abstractions'
$neutralPackageVersion = '0.1.0-preview.2'

if ($PackageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
    throw "PackageVersion '$PackageVersion' is not a supported NuGet version string."
}

$resolvedArtifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
foreach ($packageId in $packageIds) {
    $packagePath = Join-Path $resolvedArtifacts "$packageId.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Required package artifact is missing: $packagePath"
    }
}

$runId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "penghou-workflow-closure-$runId"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$runDirectory = Join-Path $outputRoot "closure-$runId"
$consumerDirectory = Join-Path $runDirectory 'consumer'
$cacheDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "pzc-$runId"
$logsDirectory = Join-Path $runDirectory 'logs'
New-Item -ItemType Directory -Path $consumerDirectory, $cacheDirectory, $logsDirectory -Force | Out-Null

function Get-NuspecDocument {
    param([Parameter(Mandatory = $true)][string] $PackagePath)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($nuspecEntry.Count -ne 1) {
            throw "Expected exactly one nuspec in '$PackagePath'; found $($nuspecEntry.Count)."
        }
        $stream = $nuspecEntry[0].Open()
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

function Get-ExactNuspecVersion {
    param([Parameter(Mandatory = $true)][string] $Version)
    return $Version.Trim('[', ']')
}

$packageEvidence = [System.Collections.Generic.List[object]]::new()
foreach ($packageId in $packageIds) {
    $packagePath = Join-Path $resolvedArtifacts "$packageId.$PackageVersion.nupkg"
    $document = Get-NuspecDocument -PackagePath $packagePath
    $metadata = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "The nuspec in '$packagePath' has no metadata element."
    }
    $actualId = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $actualVersion = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if ($actualId -cne $packageId -or $actualVersion -cne $PackageVersion) {
        throw "Artifact identity mismatch in '$packagePath': found '$actualId' '$actualVersion'."
    }

    $nuspecText = $document.OuterXml
    if ($nuspecText -match '(?i)Penghou\.Hufu') {
        throw "Forbidden Hufu dependency or reference found in '$packagePath'."
    }

    $dependencies = @($metadata.SelectNodes(".//*[local-name()='dependency']"))
    if ($packageId -ceq 'Penghou.Zhinu') {
        $neutralDependencies = @($dependencies | Where-Object { $_.GetAttribute('id') -ceq $neutralPackageId })
        if ($neutralDependencies.Count -eq 0) {
            throw "Core package '$packagePath' does not depend on $neutralPackageId."
        }
        foreach ($dependency in $neutralDependencies) {
            if ((Get-ExactNuspecVersion $dependency.GetAttribute('version')) -cne $neutralPackageVersion -or
                $dependency.GetAttribute('version') -cne "[$neutralPackageVersion]") {
                throw "Core package '$packagePath' must pin $neutralPackageId exactly as [$neutralPackageVersion]; found '$($dependency.GetAttribute('version'))'."
            }
        }
    }
    foreach ($dependency in $dependencies) {
        $dependencyId = $dependency.GetAttribute('id')
        if ($dependencyId -match '(?i)Penghou\.Hufu') {
            throw "Forbidden Hufu dependency '$dependencyId' found in '$packagePath'."
        }
        if ($dependencyId.StartsWith('Penghou.Zhinu', [StringComparison]::Ordinal) -and
            (Get-ExactNuspecVersion $dependency.GetAttribute('version')) -cne $PackageVersion) {
            throw "Internal dependency '$dependencyId' in '$packagePath' does not match package version '$PackageVersion'."
        }
    }

    $packageEvidence.Add([ordered]@{
        Id = $actualId
        Version = $actualVersion
        File = [System.IO.Path]::GetFileName($packagePath)
        DirectDependencies = @($dependencies | ForEach-Object {
            [ordered]@{ Id = $_.GetAttribute('id'); Version = $_.GetAttribute('version') }
        })
    })
}

$xmlEscape = [System.Security.SecurityElement]::Escape($resolvedArtifacts)
$cacheEscape = [System.Security.SecurityElement]::Escape($cacheDirectory)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="zhinu-local" value="$xmlEscape" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="zhinu-local">
      <package pattern="Penghou.Zhinu" />
      <package pattern="Penghou.Zhinu.Agents" />
      <package pattern="Penghou.Zhinu.Hosting" />
      <package pattern="Penghou.Zhinu.Hosting.AspNetCore" />
      <package pattern="Penghou.Zhinu.OpenTelemetry" />
      <package pattern="Penghou.Zhinu.Sqlite" />
      <package pattern="Penghou.Zhinu.Testing" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="$neutralPackageId" />
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <config>
    <add key="globalPackagesFolder" value="$cacheEscape" />
  </config>
</configuration>
"@
$nugetConfigPath = Join-Path $runDirectory 'NuGet.Config'
Set-Content -LiteralPath $nugetConfigPath -Value $nugetConfig -Encoding utf8

$projectReferenceLines = [System.Collections.Generic.List[string]]::new()
foreach ($packageId in $packageIds) {
    if ($packageId -ceq 'Penghou.Zhinu.Hosting.AspNetCore') {
        $projectReferenceLines.Add("    <PackageReference Include=`"$packageId`" Version=`"$PackageVersion`" Condition=`"'`$(TargetFramework)' == 'net10.0'`" />")
    }
    else {
        $projectReferenceLines.Add("    <PackageReference Include=`"$packageId`" Version=`"$PackageVersion`" />")
    }
}
$referencesXml = $projectReferenceLines -join "`n"
$projectFile = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
$referencesXml
  </ItemGroup>
</Project>
"@
$projectPath = Join-Path $consumerDirectory 'ClosureConsumer.csproj'
Set-Content -LiteralPath $projectPath -Value $projectFile -Encoding utf8
if (Select-String -LiteralPath $projectPath -Pattern '<ProjectReference\b' -Quiet) {
    throw 'The package closure consumer must not contain a ProjectReference.'
}

$program = @'
using Penghou.Workflow.Abstractions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

var databasePath = Path.Combine(AppContext.BaseDirectory, "closure-consumer.db");
if (File.Exists(databasePath)) File.Delete(databasePath);
var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
var ordinary = new OrdinaryWorkflow();
var protectedWorkflow = new ProtectedWorkflow();
var registry = new WorkflowRegistry()
    .Register("ordinary", "1", ordinary)
    .Register("protected", "1", protectedWorkflow);

await using (var directEngine = new WorkflowEngine(store, registry, new ZhinuOptions()))
{
    var result = await directEngine.RunAsync<string, string>("ordinary", "1", "package-closure");
    if (result != "ordinary:package-closure" || ordinary.Calls != 1)
        throw new InvalidOperationException("Direct-constructor ordinary execution did not complete exactly once.");
}

var authorizer = new AllowAuthorizer();
var authorization = new WorkflowExecutionAuthorizationOptions("closure-allow", "package-closure-v1", authorizer);
var options = new ZhinuOptions { ExecutionAuthorization = authorization };
await using (var builderEngine = new WorkflowEngineBuilder()
    .WithStore(store)
    .WithRegistry(registry)
    .WithOptions(options)
    .Build())
{
    var result = await builderEngine.RunAsync<string, string>("protected", "1", "package-closure");
    if (result != "protected:package-closure" || protectedWorkflow.Calls != 1)
        throw new InvalidOperationException("Builder protected execution did not complete exactly once.");
    if (authorizer.Contexts.Count != 1 || authorizer.Contexts[0].Requirements.Count != 1 ||
        authorizer.Contexts[0].Requirements.Single().Capability != "execute")
        throw new InvalidOperationException("Protected execution did not pass its declared requirement to the authorizer.");
}

Console.WriteLine("Package closure consumer completed both direct SQLite and protected builder execution.");

sealed class OrdinaryWorkflow : IWorkflow<string, string>
{
    public int Calls { get; private set; }
    public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
        context.StepAsync("ordinary-step", input, (value, _) =>
        {
            Calls++;
            return Task.FromResult($"ordinary:{value}");
        }, cancellationToken: cancellationToken);
}

sealed class ProtectedWorkflow : IWorkflow<string, string>
{
    public int Calls { get; private set; }
    public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
        context.StepAsync("protected-step", input, (value, _) =>
        {
            Calls++;
            return Task.FromResult($"protected:{value}");
        }, new StepOptions
        {
            Authorization = new WorkflowAuthorizationDeclaration([
                new ExecutionRequirement("workflow.capability", 1, "execute", "package-closure", null)])
        }, cancellationToken);
}

sealed class AllowAuthorizer : IExecutionAuthorizer
{
    public List<ExecutionAuthorizationContext> Contexts { get; } = [];

    public ValueTask<ExecutionAuthorizationResult> AuthorizeAsync(
        ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
    {
        Contexts.Add(context);
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new ExecutionAuthorizationResult(
            ExecutionAuthorizationDecision.Allowed,
            context.AuthorizationRequestId,
            "closure-allow",
            Guid.NewGuid().ToString("N"),
            now,
            now.AddMinutes(2)));
    }
}
'@
$programPath = Join-Path $consumerDirectory 'Program.cs'
Set-Content -LiteralPath $programPath -Value $program -Encoding utf8

function Invoke-DotNetChecked {
    param(
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [Parameter(Mandatory = $true)][string] $LogPath
    )

    Push-Location $consumerDirectory
    try {
        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Set-Content -LiteralPath $LogPath -Encoding utf8
    if ($exitCode -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode. See '$LogPath'.`n$($output -join [Environment]::NewLine)"
    }
}

function ConvertTo-NormalizedPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('/', '\'))
}

$restoreLog = Join-Path $logsDirectory 'restore.log'
Invoke-DotNetChecked -Arguments @(
    'restore', $projectPath,
    '--configfile', $nugetConfigPath,
    '-p:NuGetAudit=false',
    "-p:RestorePackagesPath=$cacheDirectory"
) -LogPath $restoreLog

$assetsPath = Join-Path $consumerDirectory 'obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
    throw "Restore did not produce expected assets file '$assetsPath'."
}
$assetsJson = Get-Content -LiteralPath $assetsPath -Raw
if ($assetsJson -match '(?i)Penghou\.Hufu') {
    throw 'Forbidden Hufu dependency or path found in project.assets.json.'
}
$assets = $assetsJson | ConvertFrom-Json
if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -ne 'package' }).Count -gt 0) {
    throw 'The isolated consumer resolved a non-package library.'
}
$assetLibraryKeys = @($assets.libraries.PSObject.Properties.Name)
if ($assetLibraryKeys | Where-Object { $_ -match '(?i)Penghou\.Hufu' }) {
    throw "Forbidden Hufu library found in restored assets: $($assetLibraryKeys -join ', ')"
}
foreach ($packageId in $packageIds) {
    $entry = @($assetLibraryKeys | Where-Object { $_ -ceq "$packageId/$PackageVersion" })
    if ($packageId -ceq 'Penghou.Zhinu.Hosting.AspNetCore') {
        if (-not $entry.Count) {
            # This package is referenced only on net10; it must appear in the aggregate multi-target assets.
            throw "The ASP.NET Core package '$packageId/$PackageVersion' is missing from multi-target restore assets."
        }
    }
    elseif (-not $entry.Count) {
        throw "Required package '$packageId/$PackageVersion' is missing from restored assets."
    }
}
$neutralAsset = @($assetLibraryKeys | Where-Object { $_ -ceq "$neutralPackageId/$neutralPackageVersion" })
if (-not $neutralAsset.Count) {
    throw "The public neutral contract package '$neutralPackageId/$neutralPackageVersion' is missing from restored assets."
}
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { [System.IO.Path]::GetFullPath($_) })
if ($packageFolders.Count -ne 1 -or
    (ConvertTo-NormalizedPath $packageFolders[0]) -ine (ConvertTo-NormalizedPath $cacheDirectory)) {
    throw "Restore used an unexpected package cache folder: $($packageFolders -join ', '). Expected only '$cacheDirectory'."
}

$targetResults = [System.Collections.Generic.List[object]]::new()
foreach ($framework in @('net8.0', 'net10.0')) {
    $runLog = Join-Path $logsDirectory "run-$framework.log"
    Invoke-DotNetChecked -Arguments @(
        'run', '--project', $projectPath,
        '--framework', $framework,
        '--no-restore'
    ) -LogPath $runLog
    $assetTargetName = switch ($framework) {
        'net8.0' { '.NETCoreApp,Version=v8.0' }
        'net10.0' { '.NETCoreApp,Version=v10.0' }
    }
    $targetAssets = $assets.targets.PSObject.Properties | Where-Object { $_.Name -ceq $assetTargetName -or $_.Name -ceq $framework } | Select-Object -First 1
    if ($null -eq $targetAssets) {
        throw "No resolved assets target found for $framework."
    }
    $targetKeys = @($targetAssets.Value.PSObject.Properties.Name)
    if ($targetKeys | Where-Object { $_ -match '(?i)Penghou\.Hufu' }) {
        throw "Forbidden Hufu package found in $framework target assets."
    }
    if ($framework -eq 'net10.0' -and -not ($targetKeys | Where-Object { $_ -like "$($packageIds[3])/*" })) {
        throw "The net10.0 target did not resolve $($packageIds[3])."
    }
    if ($framework -eq 'net8.0' -and ($targetKeys | Where-Object { $_ -like "$($packageIds[3])/*" })) {
        throw "The net8.0 target unexpectedly resolved the net10-only ASP.NET Core package."
    }
    $targetResults.Add([ordered]@{
        TargetFramework = $framework
        Result = 'passed'
        AssetsTarget = $targetAssets.Name
        Log = [System.IO.Path]::GetFileName($runLog)
    })
}

$qualification = [ordered]@{
    SchemaVersion = 1
    PackageVersion = $PackageVersion
    NeutralPackage = [ordered]@{ Id = $neutralPackageId; ExactVersion = $neutralPackageVersion }
    ArtifactsDirectory = $resolvedArtifacts
    PackageArtifacts = $packageEvidence.ToArray()
    Consumer = [ordered]@{
        Project = 'NuGet package references only; no project references'
        PackageSourceMapping = [ordered]@{
            ZhinuPackages = 'Local artifacts directory, exact package ID patterns'
            NeutralAndExternalPackages = 'nuget.org'
        }
        PackageCache = $cacheDirectory
        AssetsFile = [System.IO.Path]::GetRelativePath($runDirectory, $assetsPath)
        ResolvedPackageIds = @($assetLibraryKeys)
        ForbiddenHufuDependencyScan = 'passed'
    }
    TargetFrameworkResults = $targetResults.ToArray()
    Status = 'passed'
}
$qualificationPath = Join-Path $runDirectory 'qualification.json'
$qualification | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $qualificationPath -Encoding utf8
Write-Output $qualificationPath
