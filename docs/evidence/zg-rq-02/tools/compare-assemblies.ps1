#Requires -Version 7.0
# Compare two managed assemblies at the metadata/IL level.
# Usage: compare-assemblies.ps1 <baseline.dll> <candidate.dll>
param(
    [Parameter(Mandatory = $true)][string] $Baseline,
    [Parameter(Mandatory = $true)][string] $Candidate
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata

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
                    $il = $body.GetILBytes()
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try { $ilHash = [Convert]::ToHexString($sha.ComputeHash($il)).ToLowerInvariant() }
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
            return [ordered]@{
                mvid = $mvid
                typeCount = $types.Count
                methodCount = $methods.Count
                types = ($types | Sort-Object)
                methods = $methods
                references = ($refs | Sort-Object)
                debugDirectoryEntries = @($pe.ReadDebugDirectory() | ForEach-Object { $_.Type.ToString() })
                hasDeterministicDebugDirectory = @($pe.ReadDebugDirectory() | Where-Object { $_.Type -eq 'CodeView' }).Count -gt 0
            }
        }
        finally { $pe.Dispose() }
    }
    finally { $stream.Dispose() }
}

$base = Get-AssemblyShape $Baseline
$cand = Get-AssemblyShape $Candidate
$typeDiff = Compare-Object $base.types $cand.types
$refDiff = Compare-Object $base.references $cand.references
$methodKeys = @($base.methods.Keys) + @($cand.methods.Keys) | Sort-Object -Unique
$methodDiff = @()
foreach ($key in $methodKeys) {
    $b = if ($base.methods.Contains($key)) { $base.methods[$key] } else { '<missing>' }
    $c = if ($cand.methods.Contains($key)) { $cand.methods[$key] } else { '<missing>' }
    if ($b -ne $c) { $methodDiff += $key }
}
[ordered]@{
    baseline = [ordered]@{ file = $Baseline; mvid = $base.mvid; types = $base.typeCount; methods = $base.methodCount; debug = $base.debugDirectoryEntries }
    candidate = [ordered]@{ file = $Candidate; mvid = $cand.mvid; types = $cand.typeCount; methods = $cand.methodCount; debug = $cand.debugDirectoryEntries }
    typesIdentical = ($null -eq $typeDiff)
    referencesIdentical = ($null -eq $refDiff)
    ilIdentical = ($methodDiff.Count -eq 0)
    differingMethods = $methodDiff
} | ConvertTo-Json -Depth 5
