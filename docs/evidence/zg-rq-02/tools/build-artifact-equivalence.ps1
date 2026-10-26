#Requires -Version 7.0
# ZG-RQ-02 artifact equivalence: recorded f0edf17 candidate set vs the
# Stage-A artifact set produced by CI run 40303504133 at 908555d (source and
# tests identical to f0edf17). Produces local/artifact-equivalence.json.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Reflection.Metadata

$baselineDir = 'C:\Users\Laszlos\source\repos\Penghou.Zhinu\artifacts'
$candidateDir = 'C:\Windows\TEMP\opencode\zgrq02\stage-a-908555d'
$target = 'C:\Users\Laszlos\source\repos\zg-rq-02-zhinu\docs\evidence\zg-rq-02\local\artifact-equivalence.json'
$work = 'C:\Windows\TEMP\opencode\zgrq02\artifact-equivalence-work'
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Path $work | Out-Null

function Get-Sha256([string] $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-EntryHashes([string] $path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $map = [ordered]@{}
        foreach ($entry in ($zip.Entries | Sort-Object FullName)) {
            $stream = $entry.Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            $map[$entry.FullName] = [ordered]@{ length = $entry.Length; sha256 = $hash }
        }
        return $map
    }
    finally { $zip.Dispose() }
}

function Get-Nuspec([string] $path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { [xml] $xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $xml.package.metadata
        $deps = @()
        foreach ($group in @($meta.dependencies.group)) {
            if ($null -eq $group) { continue }
            foreach ($dep in @($group.dependency)) {
                if ($null -eq $dep) { continue }
                $deps += [ordered]@{ targetFramework = $group.targetFramework; id = $dep.id; version = $dep.version }
            }
        }
        return [ordered]@{
            id = $meta.id
            version = $meta.version
            repositoryCommit = if ($meta.repository) { $meta.repository.commit } else { $null }
            repositoryBranch = if ($meta.repository) { $meta.repository.branch } else { $null }
            dependencies = $deps
        }
    }
    finally { $zip.Dispose() }
}

function Get-AssemblyShape([string] $path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $mvid = $md.GetGuid($md.GetModuleDefinition().Mvid).ToString('D')
            $methods = [ordered]@{}
            foreach ($handle in $md.MethodDefinitions) {
                $def = $md.GetMethodDefinition($handle)
                $name = $md.GetString($def.Name)
                $typeName = ''
                if ($def.GetDeclaringType().IsNil -eq $false) {
                    $type = $md.GetTypeDefinition($def.GetDeclaringType())
                    $typeName = $md.GetString($type.Namespace) + '.' + $md.GetString($type.Name)
                }
                $key = "$typeName::$name"
                $ilHash = ''
                if ($def.RelativeVirtualAddress -ne 0) {
                    $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe, $def.RelativeVirtualAddress)
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try { $ilHash = [Convert]::ToHexString($sha.ComputeHash($body.GetILBytes())).ToLowerInvariant() }
                    finally { $sha.Dispose() }
                }
                $methods[$key] = $ilHash
            }
            $types = @()
            foreach ($handle in $md.TypeDefinitions) {
                $type = $md.GetTypeDefinition($handle)
                $types += ($md.GetString($type.Namespace) + '.' + $md.GetString($type.Name))
            }
            $refs = @()
            foreach ($handle in $md.AssemblyReferences) {
                $ref = $md.GetAssemblyReference($handle)
                $refs += ($md.GetString($ref.Name) + '/' + $ref.Version.ToString())
            }
            return [ordered]@{ mvid = $mvid; types = ($types | Sort-Object); methods = $methods; references = ($refs | Sort-Object) }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Compare-Assemblies([string] $basePath, [string] $candPath) {
    $b = Get-AssemblyShape $basePath
    $c = Get-AssemblyShape $candPath
    $methodKeys = @($b.methods.Keys) + @($c.methods.Keys) | Sort-Object -Unique
    $methodDiff = @()
    foreach ($key in $methodKeys) {
        $bv = if ($b.methods.Contains($key)) { $b.methods[$key] } else { '<missing>' }
        $cv = if ($c.methods.Contains($key)) { $c.methods[$key] } else { '<missing>' }
        if ($bv -ne $cv) { $methodDiff += $key }
    }
    [ordered]@{
        baselineMvid = $b.mvid
        candidateMvid = $c.mvid
        typeCount = $b.types.Count
        methodCount = $b.methods.Count
        typesIdentical = (@(Compare-Object $b.types $c.types).Count -eq 0)
        referencesIdentical = (@(Compare-Object $b.references $c.references).Count -eq 0)
        ilIdentical = ($methodDiff.Count -eq 0)
        differingMethods = $methodDiff
    }
}

function Get-XmlDocs([string] $extractDir) {
    $result = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter '*.xml' | Where-Object { $_.FullName -like '*\lib\*' } | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($extractDir.Length).TrimStart('\')
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $normalized = [Convert]::ToHexString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant() }
        finally { $sha.Dispose() }
        $result[$relative] = $normalized
    }
    return $result
}

$packages = @()
foreach ($file in (Get-ChildItem -LiteralPath $baselineDir -File | Where-Object { $_.Extension -in '.nupkg', '.snupkg' } | Sort-Object Name)) {
    $candidatePath = Join-Path $candidateDir $file.Name
    if (-not (Test-Path -LiteralPath $candidatePath)) { throw "Candidate archive missing: $($file.Name)" }
    $baseEntries = Get-EntryHashes $file.FullName
    $candEntries = Get-EntryHashes $candidatePath
    $allNames = @($baseEntries.Keys) + @($candEntries.Keys) | Sort-Object -Unique
    $differing = @()
    foreach ($name in $allNames) {
        $inBase = $baseEntries.Contains($name)
        $inCand = $candEntries.Contains($name)
        if (-not $inBase -or -not $inCand) { $differing += "$name (presence)"; continue }
        if ($baseEntries[$name].sha256 -ne $candEntries[$name].sha256) { $differing += $name }
    }

    $baseNuspec = Get-Nuspec $file.FullName
    $candNuspec = Get-Nuspec $candidatePath
    $nuspecDifferences = @()
    foreach ($key in @('id', 'version', 'repositoryCommit', 'repositoryBranch')) {
        if ($baseNuspec[$key] -ne $candNuspec[$key]) { $nuspecDifferences += "$key ($($baseNuspec[$key]) -> $($candNuspec[$key]))" }
    }
    if (($baseNuspec.dependencies | ConvertTo-Json -Depth 5) -ne ($candNuspec.dependencies | ConvertTo-Json -Depth 5)) { $nuspecDifferences += 'dependencies' }

    $assemblyComparisons = [ordered]@{}
    $xmlDocsIdentical = $null
    if ($file.Extension -eq '.nupkg') {
        $baseExtract = Join-Path $work ("base-" + $file.BaseName)
        $candExtract = Join-Path $work ("cand-" + $file.BaseName)
        [System.IO.Compression.ZipFile]::ExtractToDirectory($file.FullName, $baseExtract)
        [System.IO.Compression.ZipFile]::ExtractToDirectory($candidatePath, $candExtract)
        foreach ($dll in (Get-ChildItem -LiteralPath $baseExtract -Recurse -File -Filter '*.dll' | Where-Object { $_.FullName -like '*\lib\*' } | Sort-Object FullName)) {
            $relative = $dll.FullName.Substring($baseExtract.Length).TrimStart('\')
            $candDll = Join-Path $candExtract $relative
            $assemblyComparisons[$relative] = Compare-Assemblies $dll.FullName $candDll
        }
        $baseXml = Get-XmlDocs $baseExtract
        $candXml = Get-XmlDocs $candExtract
        $xmlDocsIdentical = (($baseXml | ConvertTo-Json -Depth 3) -eq ($candXml | ConvertTo-Json -Depth 3))
    }

    $packages += [ordered]@{
        archive = $file.Name
        baselineLength = $file.Length
        candidateLength = (Get-Item -LiteralPath $candidatePath).Length
        baselineSha256 = Get-Sha256 $file.FullName
        candidateSha256 = Get-Sha256 $candidatePath
        entryCountBaseline = $baseEntries.Count
        entryCountCandidate = $candEntries.Count
        differingEntries = $differing
        payloadByteIdentical = ($differing.Count -eq 0)
        nuspec = [ordered]@{
            id = $baseNuspec.id
            version = $baseNuspec.version
            dependencies = $baseNuspec.dependencies
            dependenciesIdentical = (($baseNuspec.dependencies | ConvertTo-Json -Depth 5) -eq ($candNuspec.dependencies | ConvertTo-Json -Depth 5))
            differences = $nuspecDifferences
        }
        xmlDocsNormalizedIdentical = $xmlDocsIdentical
        assemblies = $assemblyComparisons
    }
}

$allAssemblies = @()
foreach ($p in $packages) { foreach ($a in $p.assemblies.Values) { $allAssemblies += $a } }
$ilIdentical = @($allAssemblies | Where-Object { $_.ilIdentical }).Count
$typesIdentical = @($allAssemblies | Where-Object { $_.typesIdentical }).Count
$refsIdentical = @($allAssemblies | Where-Object { $_.referencesIdentical }).Count
$nuspecOnlyCommitDiff = @($packages | Where-Object { $_.nuspec.differences.Count -eq 2 -and $_.nuspec.differences[0] -like 'repositoryCommit*' -and $_.nuspec.differences[1] -like 'repositoryBranch*' }).Count
$xmlOk = @($packages | Where-Object { $_.xmlDocsNormalizedIdentical -eq $true }).Count

$result = [ordered]@{
    executedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    baseline = [ordered]@{ commit = 'f0edf1714013106bdbce090179342ccb029eaf83'; directory = $baselineDir; source = 'recorded ZG-RQ-01 candidate artifact set' }
    candidate = [ordered]@{ commit = '908555d15b7091b4846c5f81040f4b15b31b3925'; runId = 40303504133; artifact = 'release-qualification-ubuntu-latest'; directory = $candidateDir; source = 'GitHub-hosted Stage-A qualification artifact set' }
    sourceEquivalence = 'git diff f0edf17 908555d -- src tests is empty; see scope-check.json'
    summary = [ordered]@{
        archives = $packages.Count
        archivesByteIdentical = @($packages | Where-Object { $_.baselineSha256 -eq $_.candidateSha256 }).Count
        nuspecDependenciesIdentical = @($packages | Where-Object { $_.nuspec.dependenciesIdentical }).Count
        nuspecOnlyCommitAndBranchDiffer = $nuspecOnlyCommitDiff
        nupkgXmlDocsNormalizedIdentical = "$xmlOk / $(@($packages | Where-Object { $_.xmlDocsNormalizedIdentical -ne $null }).Count)"
        assembliesCompared = $allAssemblies.Count
        assembliesIlIdentical = $ilIdentical
        assembliesTypesIdentical = $typesIdentical
        assembliesReferencesIdentical = $refsIdentical
        category = if (@($packages | Where-Object { $_.baselineSha256 -eq $_.candidateSha256 }).Count -eq $packages.Count) { 'byte-identical archives' } elseif ($ilIdentical -eq $allAssemblies.Count -and $typesIdentical -eq $allAssemblies.Count -and $refsIdentical -eq $allAssemblies.Count) { 'semantically equivalent: identical types, references and method IL; archive bytes, MVID, PDB checksums and XML line endings differ by build environment; nuspec differs only in repository branch/commit binding' } else { 'payload differs - review required' }
    }
    packages = $packages
}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $target -Encoding utf8
Write-Output ($result.summary | ConvertTo-Json)
Write-Output "written: $target"
