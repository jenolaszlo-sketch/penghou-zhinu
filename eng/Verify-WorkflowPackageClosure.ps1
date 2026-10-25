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
# CI writes this consumer beneath the repository. Do not inherit its library
# versioning, packability, analyzers or future build targets into a package-only app.
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.props') -Value '<Project />' -Encoding utf8
Set-Content -LiteralPath (Join-Path $consumerDirectory 'Directory.Build.targets') -Value '<Project />' -Encoding utf8
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
var groupManifest = ActivityGroupManifest.Create([
    new ActivityGroupDefinition("root", ActivityGroupKind.Logical, "Root"),
    new ActivityGroupDefinition("work", ActivityGroupKind.Logical, "Work", "root")]);
var nestedManifest = ActivityGroupManifest.Create([
    new ActivityGroupDefinition("outer", ActivityGroupKind.Logical, "Outer"),
    new ActivityGroupDefinition("inner", ActivityGroupKind.Logical, "Inner", "outer"),
    new ActivityGroupDefinition("leaf", ActivityGroupKind.Logical, "Leaf", "inner")]);
var registry = new WorkflowRegistry()
    .Register("ordinary", "1", ordinary)
    .Register("protected", "1", protectedWorkflow)
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped", Version = "1" }, () => new GroupedWorkflow(), groupManifest))
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped-fanout", Version = "1" }, () => new GroupedFanOutWorkflow(), groupManifest))
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped-nested", Version = "1" }, () => new GroupedNestedWorkflow(), nestedManifest))
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped-epochs", Version = "1" }, () => new GroupedEpochWorkflow(), groupManifest))
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped-parent", Version = "1" }, () => new GroupedChildWorkflow(true), groupManifest))
    .Register(new WorkflowRegistration<string, string>(
        new WorkflowDefinition { Name = "grouped-child", Version = "1" }, () => new GroupedChildWorkflow(false), groupManifest));

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
    var groupedId = await builderEngine.StartAsync("grouped", "1", "package-closure");
    await builderEngine.ExecuteAsync(groupedId);
    var groupedResult = await builderEngine.WaitForCompletionAsync<string>(groupedId);
    // The configured authority profile also evaluates loop controls with empty
    // requirements. Only the two body callbacks declare the execute capability.
    var declaredGroupContexts = authorizer.Contexts.Skip(1).Where(context => context.Requirements.Count > 0).ToArray();
    if (groupedResult != "2" || declaredGroupContexts.Length != 2 ||
        declaredGroupContexts.Any(context => context.Requirements.Count != 1 || context.Requirements.Single().Capability != "execute"))
        throw new InvalidOperationException("Grouped loop execution lost its result or authorization declarations.");
    var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath, Pooling = false });
    if ((await reopened.GetGroupManifestAsync(groupedId))?.CanonicalJson != groupManifest.CanonicalJson)
        throw new InvalidOperationException("Grouped manifest was not durably installed.");
    var groupedSteps = (await reopened.GetStepsAsync(groupedId)).Where(s => s.StepKey.EndsWith("/body/work", StringComparison.Ordinal)).ToArray();
    if (groupedSteps.Length != 2) throw new InvalidOperationException("Grouped loop did not repeat twice.");
    var occurrenceIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var step in groupedSteps)
    {
        var membership = await reopened.GetStepGroupMembershipAsync(step.Id)
            ?? throw new InvalidOperationException("Grouped revision membership was lost.");
        var occurrence = await reopened.GetGroupOccurrenceAsync(groupedId, membership.GroupOccurrenceId)
            ?? throw new InvalidOperationException("Grouped occurrence was lost.");
        if (membership.StepRevision != step.Revision || occurrence.GroupKey != "work" || occurrence.ParentGroupOccurrenceId is null)
            throw new InvalidOperationException("Grouped revision or parent association changed.");
        occurrenceIds.Add(occurrence.GroupOccurrenceId);
    }
    if (occurrenceIds.Count != 2) throw new InvalidOperationException("Repeated group occurrences collided.");
    var groupForkId = await builderEngine.ForkAsync(groupedId, "new-step");
    await builderEngine.ExecuteAsync(groupForkId);
    if (await builderEngine.WaitForCompletionAsync<string>(groupForkId) != "2" ||
        (await reopened.GetRunAsync(groupForkId))?.SourceRunId != groupedId ||
        (await reopened.GetGroupManifestAsync(groupForkId))?.CanonicalJson != groupManifest.CanonicalJson)
        throw new InvalidOperationException("Grouped fork lost native reuse, source lineage or destination manifest.");
    var copiedGroupSteps = (await reopened.GetStepsAsync(groupForkId)).Where(step => step.StepKey.EndsWith("/body/work", StringComparison.Ordinal)).ToArray();
    if (copiedGroupSteps.Length != 2) throw new InvalidOperationException("Grouped fork lost copied loop members.");
    foreach (var copied in copiedGroupSteps)
    {
        var membership = await reopened.GetStepGroupMembershipAsync(copied.Id)
            ?? throw new InvalidOperationException("Grouped fork lost copied membership.");
        var occurrence = await reopened.GetGroupOccurrenceAsync(groupForkId, membership.GroupOccurrenceId)
            ?? throw new InvalidOperationException("Grouped fork lost destination occurrence.");
        if (occurrenceIds.Contains(occurrence.GroupOccurrenceId) || occurrence.WorkflowRunId != groupForkId ||
            occurrence.ParentGroupOccurrenceId is null || membership.StepRevision != copied.Revision)
            throw new InvalidOperationException("Grouped fork retained source occurrence references.");
        var parent = await reopened.GetGroupOccurrenceAsync(groupForkId, occurrence.ParentGroupOccurrenceId);
        if (parent?.WorkflowRunId != groupForkId || parent.GroupKey != "root")
            throw new InvalidOperationException("Grouped fork lost destination parent lineage.");
    }
    var contextsBeforeFanOut = authorizer.Contexts.Count;
    var fanOutId = await builderEngine.StartAsync("grouped-fanout", "1", "package-closure");
    await builderEngine.ExecuteAsync(fanOutId);
    if (await builderEngine.WaitForCompletionAsync<string>(fanOutId) != "A:B")
        throw new InvalidOperationException("Grouped fan-out changed native results.");
    var fanOutState = await reopened.GetGroupFanOutAsync(fanOutId, "items");
    if (fanOutState is not { IsComplete: true, ItemCount: 2 } ||
        (await reopened.GetRunAsync(fanOutId))?.GroupFanOutUsed != true)
        throw new InvalidOperationException("Grouped fan-out enumeration/closure/marker was not durable.");
    var fanOutContexts = authorizer.Contexts.Skip(contextsBeforeFanOut).Where(context => context.Requirements.Count > 0).ToArray();
    if (fanOutContexts.Length != 2 || fanOutContexts.Any(context => context.Requirements.Single().Capability != "execute"))
        throw new InvalidOperationException("Grouped fan-out lost its authorization declarations.");
    var fanOutIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var step in (await reopened.GetStepsAsync(fanOutId)).Where(step => step.StepKey.StartsWith("items.", StringComparison.Ordinal)))
    {
        var membership = await reopened.GetStepGroupMembershipAsync(step.Id)
            ?? throw new InvalidOperationException("Fan-out item lost its revision membership.");
        var occurrence = await reopened.GetGroupOccurrenceAsync(fanOutId, membership.GroupOccurrenceId)
            ?? throw new InvalidOperationException("Fan-out item lost its occurrence.");
        if (membership.StepRevision != step.Revision || occurrence.ParentGroupOccurrenceId is null ||
            !occurrence.ExecutionCoordinate.Contains("\"version\":2", StringComparison.Ordinal))
            throw new InvalidOperationException("Fan-out item lost its parent or v2 coordinate.");
        fanOutIds.Add(occurrence.GroupOccurrenceId);
    }
    if (fanOutIds.Count != 2) throw new InvalidOperationException("Fan-out occurrences collided.");
    var nestedId = await builderEngine.StartAsync("grouped-nested", "1", "identical-input");
    await builderEngine.ExecuteAsync(nestedId);
    if (await builderEngine.WaitForCompletionAsync<string>(nestedId) != "3" ||
        (await reopened.GetGroupManifestAsync(nestedId))?.CanonicalJson != nestedManifest.CanonicalJson)
        throw new InvalidOperationException("Nested repetition lost its result or manifest.");
    var nestedLeaves = (await reopened.GetStepsAsync(nestedId)).Where(step => step.StepKey.EndsWith("/body/work", StringComparison.Ordinal)).ToArray();
    var nestedIds = new HashSet<string>(StringComparer.Ordinal);
    var outerIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var step in nestedLeaves)
    {
        var membership = await reopened.GetStepGroupMembershipAsync(step.Id)
            ?? throw new InvalidOperationException("Nested leaf membership missing.");
        var leaf = await reopened.GetGroupOccurrenceAsync(nestedId, membership.GroupOccurrenceId)
            ?? throw new InvalidOperationException("Nested leaf occurrence missing.");
        var inner = await reopened.GetGroupOccurrenceAsync(nestedId, leaf.ParentGroupOccurrenceId!);
        var outer = await reopened.GetGroupOccurrenceAsync(nestedId, inner!.ParentGroupOccurrenceId!);
        using var coordinate = System.Text.Json.JsonDocument.Parse(leaf.ExecutionCoordinate);
        if (leaf.GroupKey != "leaf" || inner.GroupKey != "inner" || outer?.GroupKey != "outer" ||
            coordinate.RootElement.GetArrayLength() != 2 || inner.ExecutionCoordinate != leaf.ExecutionCoordinate ||
            membership.StepRevision != step.Revision || outer.ParentGroupOccurrenceId is not null)
            throw new InvalidOperationException("Nested immediate-parent lineage changed.");
        nestedIds.Add(leaf.GroupOccurrenceId);
        outerIds.Add(outer.GroupOccurrenceId);
    }
    if (nestedLeaves.Length != 6 || nestedIds.Count != 6 || outerIds.Count != 3)
        throw new InvalidOperationException("Nested repetition aliased local inner positions across outer iterations.");
    var epochsId = await builderEngine.StartAsync("grouped-epochs", "1", "same-value");
    await builderEngine.ExecuteAsync(epochsId);
    if (await builderEngine.WaitForCompletionAsync<string>(epochsId) != "five-epochs" ||
        await reopened.HasIncompleteGroupFanOutAsync(epochsId))
        throw new InvalidOperationException("Evolving selection lost an epoch obligation.");
    var epochOccurrences = new HashSet<string>(StringComparer.Ordinal);
    for (var epoch = 1; epoch <= 5; epoch++)
    {
        var scope = "epochs." + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var state = await reopened.GetGroupFanOutAsync(epochsId, scope);
        if (state is not { IsComplete: true }) throw new InvalidOperationException("An explicit epoch did not close.");
        var members = (await reopened.GetStepsAsync(epochsId)).Where(step => step.StepKey.StartsWith(scope + ".", StringComparison.Ordinal));
        foreach (var step in members)
        {
            var membership = await reopened.GetStepGroupMembershipAsync(step.Id)
                ?? throw new InvalidOperationException("Epoch member lost its history.");
            var occurrence = await reopened.GetGroupOccurrenceAsync(epochsId, membership.GroupOccurrenceId)
                ?? throw new InvalidOperationException("Epoch occurrence missing.");
            if (!epochOccurrences.Add(occurrence.GroupOccurrenceId) || occurrence.ParentGroupOccurrenceId is null)
                throw new InvalidOperationException("Identical/reappearing item aliased a prior epoch.");
        }
    }
    if (epochOccurrences.Count != 14) throw new InvalidOperationException("Evolving epoch cardinality changed.");

    var parentId = await builderEngine.StartAsync("grouped-parent", "1", "package-closure");
    await builderEngine.ExecuteAsync(parentId);
    if (await builderEngine.WaitForCompletionAsync<string>(parentId) != "package-closure:child")
        throw new InvalidOperationException("Grouped child invocation changed native output.");
    var parentSteps = await reopened.GetStepsAsync(parentId);
    string? parentOccurrence = null;
    foreach (var (key, role) in new[] { ("child:start", ActivityGroupMemberRole.Control), ("child:wait", ActivityGroupMemberRole.Work) })
    {
        var step = parentSteps.Single(step => step.StepKey == key);
        var membership = await reopened.GetStepGroupMembershipAsync(step.Id)
            ?? throw new InvalidOperationException("Child parent boundary lost membership.");
        if (membership.Role != role || (parentOccurrence is not null && parentOccurrence != membership.GroupOccurrenceId))
            throw new InvalidOperationException("Child boundary role or shared parent occurrence changed.");
        parentOccurrence = membership.GroupOccurrenceId;
    }
    var childId = System.Text.Json.JsonSerializer.Deserialize<Guid>(parentSteps.Single(step => step.StepKey == "child:start").OutputJson!);
    if ((await reopened.GetRunAsync(childId))?.ParentRunId != parentId ||
        (await reopened.GetGroupManifestAsync(childId))?.CanonicalJson != groupManifest.CanonicalJson)
        throw new InvalidOperationException("Child admission lost native parent or independent manifest.");
    var childStep = (await reopened.GetStepsAsync(childId)).Single();
    var childMember = await reopened.GetStepGroupMembershipAsync(childStep.Id)
        ?? throw new InvalidOperationException("Child work lost its own membership.");
    var childGroup = await reopened.GetGroupOccurrenceAsync(childId, childMember.GroupOccurrenceId);
    if (childGroup?.WorkflowRunId != childId || childGroup.GroupOccurrenceId == parentOccurrence ||
        childGroup.ParentGroupOccurrenceId is null ||
        (await reopened.GetGroupOccurrenceAsync(childId, childGroup.ParentGroupOccurrenceId))?.WorkflowRunId != childId ||
        await reopened.GetGroupOccurrenceAsync(parentId, childGroup.GroupOccurrenceId) is not null)
        throw new InvalidOperationException("Child group tree crossed its run boundary.");

    using var hierarchyReader = new WorkflowGroupReader(reopened);
    var hierarchy = await hierarchyReader.GetRunGroupSnapshotAsync(nestedId);
    if (hierarchy.Status != WorkflowGroupReadStatus.Success || hierarchy.Value is not { OccurrenceCount: 15, ProvenanceIncluded: false })
        throw new InvalidOperationException("Packaged hierarchy snapshot did not retain the nested tree.");
    var token = hierarchy.Value.SnapshotToken;
    var page = await hierarchyReader.QueryGroupChildrenAsync(nestedId, token, pageSize: 1);
    var rootOccurrences = new List<string>();
    while (true)
    {
        if (page.Status != WorkflowGroupReadStatus.Success) throw new InvalidOperationException("Packaged hierarchy paging failed.");
        rootOccurrences.AddRange(page.Value!.Items.Where(node => node.Occurrence is not null).Select(node => node.Occurrence!.OccurrenceId));
        if (!page.Value.HasMore) break;
        page = await hierarchyReader.QueryGroupChildrenAsync(nestedId, token, pageSize: 1, cursor: page.Value.NextCursor);
    }
    if (rootOccurrences.Count != 3 || rootOccurrences.Distinct().Count() != 3)
        throw new InvalidOperationException("Packaged hierarchy paging aliased occurrences.");
    foreach (var rootOccurrence in rootOccurrences)
    {
        var state = await hierarchyReader.GetGroupStateAsync(nestedId, token, rootOccurrence);
        if (state.Status != WorkflowGroupReadStatus.Success || state.Value!.State.Status != StepStatus.Completed ||
            state.Value.State.Work.Known != 2 || state.Value.State.TotalCount != 2 || state.Value.ObservedAt != hierarchy.Value.CapturedAt)
            throw new InvalidOperationException("Packaged aggregate double-counted containers or lost snapshot consistency.");
    }
    var parentSnapshot = await hierarchyReader.GetRunGroupSnapshotAsync(parentId);
    var parentRoots = await hierarchyReader.QueryGroupChildrenAsync(parentId, parentSnapshot.Value!.SnapshotToken);
    if (!parentRoots.Value!.Items.Any(node => node.ChildRun is { IsAvailable: true } link && link.RunId == childId && link.ParentRunId == parentId))
        throw new InvalidOperationException("Packaged hierarchy omitted the independent native child link.");
    var parentGroup = parentRoots.Value.Items.Single(node => node.Occurrence is not null).Occurrence!;
    var parentState = (await hierarchyReader.GetGroupStateAsync(parentId, parentSnapshot.Value.SnapshotToken, parentGroup.OccurrenceId)).Value!;
    if (parentState.State.Work.Known != 1 || parentState.State.Control.Known != 1 || parentState.State.Status != StepStatus.Completed)
        throw new InvalidOperationException("Packaged aggregate folded child work into its parent or lost Control roles.");
    var epochsSnapshot = (await hierarchyReader.GetRunGroupSnapshotAsync(epochsId)).Value!;
    var epochsRoot = (await hierarchyReader.QueryGroupChildrenAsync(epochsId, epochsSnapshot.SnapshotToken)).Value!.Items.Single(node => node.Occurrence is not null).Occurrence!;
    var epochState = (await hierarchyReader.GetGroupStateAsync(epochsId, epochsSnapshot.SnapshotToken, epochsRoot.OccurrenceId)).Value!;
    if (epochState.State.Status != StepStatus.Completed || epochState.State.Work.Known != 14 || epochState.State.TotalCount != 14)
        throw new InvalidOperationException("Packaged aggregate lost independently admitted epoch obligations.");
    using var otherReader = builderEngine.CreateGroupReader();
    if ((await otherReader.QueryGroupChildrenAsync(nestedId, token)).Status != WorkflowGroupReadStatus.ResyncRequired)
        throw new InvalidOperationException("A reader accepted another cache's token.");
}

Console.WriteLine("Package closure consumer completed direct SQLite, protected builder, grouped loop/fork/fan-out, 3x2 nested repetition, five immutable evolving-selection epochs, independent grouped child boundaries, bounded hierarchy query paging and coherent aggregate state/progress.");

sealed class GroupedChildWorkflow(bool parent) : IWorkflow<string, string>
{
    public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var root = context.GetActivityGroup("root");
        var work = context.GetActivityGroup("work", root);
        var binding = new ActivityGroupStepBinding(new ActivityGroupMembership("work"), work);
        return parent
            ? context.StartChildAsync<string, string>("child", "grouped-child", "1", input, new ChildRunOptions
            {
                StartGroup = new ActivityGroupStepBinding(new ActivityGroupMembership("work", ActivityGroupMemberRole.Control), work),
                WaitGroup = binding
            }, cancellationToken)
            : context.StepAsync("work", input, (value, _) => Task.FromResult(value + ":child"),
                new StepOptions { Group = binding }, cancellationToken);
    }
}

sealed class GroupedEpochWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var schedule = await context.StepAsync("epoch-schedule", input, (_, _, _) => Task.FromResult(new[] { 1, 2, 3, 4, 5 }), cancellationToken: cancellationToken);
        foreach (var epoch in schedule)
        {
            var suffix = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var selection = "selection." + suffix;
            var selected = await context.StepAsync(selection, epoch, (value, _, _) => Task.FromResult(value switch
            {
                1 => new[] { "A", "B", "C" },
                2 => new[] { "A", "C", "D", "E" },
                3 => new[] { "C", "A" },
                4 => new[] { "A", "C" },
                _ => new[] { "B", "A", "C" }
            }), cancellationToken: cancellationToken);
            var result = await context.FanOutAsync("epochs." + suffix, selected, key => key,
                (key, _, _) => Task.FromResult(key + ":" + input), new FanOutOptions { MaxDegreeOfParallelism = 1 },
                new StepOptions
                {
                    DependsOn = [selection],
                    FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), selection, context.GetActivityGroup("root"))
                }, cancellationToken);
            if (!result.SequenceEqual(selected.Select(key => key + ":" + input), StringComparer.Ordinal))
                throw new InvalidOperationException("Epoch ordering changed historical item ownership.");
        }
        return "five-epochs";
    }
}

sealed class GroupedNestedWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var result = await context.LoopAsync("outer", 0, state => state < 3, async (outer, ct) =>
        {
            var outerGroup = outer.GetActivityGroup("outer");
            await outer.LoopAsync("inner", 0, state => state < 2, async (inner, innerCt) =>
            {
                var innerGroup = inner.GetActivityGroup("inner", outerGroup);
                var leaf = inner.GetActivityGroup("leaf", innerGroup);
                await inner.StepAsync("work", input, (value, _, _) => Task.FromResult(value), new StepOptions
                {
                    Group = new ActivityGroupStepBinding(new ActivityGroupMembership("leaf"), leaf)
                }, innerCt);
                return inner.Continue(inner.State + 1);
            }, new LoopOptions(2), ct);
            return outer.Continue(outer.State + 1);
        }, new LoopOptions(3), cancellationToken);
        return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

sealed class GroupedFanOutWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var selected = await context.StepAsync("select", input, (_, _) => Task.FromResult(new[] { "a", "b" }), cancellationToken: cancellationToken);
        var values = await context.FanOutAsync("items", selected, value => value,
            (value, _, _) => Task.FromResult(value.ToUpperInvariant()), new FanOutOptions { MaxDegreeOfParallelism = 1 },
            new StepOptions
            {
                DependsOn = ["select"],
                FanOutGroup = new ActivityGroupFanOutBinding(new ActivityGroupMembership("work"), "select", context.GetActivityGroup("root")),
                Authorization = new WorkflowAuthorizationDeclaration([
                    new ExecutionRequirement("workflow.capability", 1, "execute", "package-closure", null)])
            }, cancellationToken);
        return string.Join(':', values);
    }
}

sealed class GroupedWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
    {
        var root = context.GetActivityGroup("root");
        var result = await context.LoopAsync("repeat", 0, state => state < 2, async (iteration, ct) =>
        {
            var group = iteration.GetActivityGroup("work", root);
            await iteration.StepAsync("work", input, (value, _, _) => Task.FromResult(value), new StepOptions
            {
                Group = new ActivityGroupStepBinding(new ActivityGroupMembership("work"), group),
                Authorization = new WorkflowAuthorizationDeclaration([
                    new ExecutionRequirement("workflow.capability", 1, "execute", "package-closure", null)])
            }, ct);
            return iteration.Continue(iteration.State + 1);
        }, new LoopOptions(2), cancellationToken);
        return result.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

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
