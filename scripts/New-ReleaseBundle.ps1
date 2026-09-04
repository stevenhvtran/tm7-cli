<#
.SYNOPSIS
    Assembles the final tm7 release bundle from the per-RID archives produced by
    Publish-NativeAsset.ps1.

.DESCRIPTION
    Collects every tm7-<rid>.zip / tm7-<rid>.tar.gz under the input directory,
    copies them into the output directory, and generates:
      * checksums.txt          sha256sum-compatible (`<hash>  <name>`) for every archive
      * release-metadata.json   { version, assets[] } describing the published assets

    The output directory contents are exactly what gets uploaded to the GitHub
    Release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InputDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Version,

    # When > 0, fail unless exactly this many release archives are present. Guards
    # against publishing a partial release if an artifact failed to upload/download.
    [int]$ExpectedAssetCount = 0
)

$ErrorActionPreference = 'Stop'

$inputDirectory = [System.IO.Path]::GetFullPath($InputDirectory)
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path $inputDirectory)) {
    throw "Input directory '$inputDirectory' does not exist."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

# Collect the release archives produced by Publish-NativeAsset.ps1 (ignore the
# per-RID .json sidecars; we regenerate the consolidated metadata below).
$archives = Get-ChildItem -Path $inputDirectory -Recurse -File |
    Where-Object { $_.Name -match '^tm7-.*\.(zip|tar\.gz)$' } |
    Sort-Object Name

if ($archives.Count -eq 0) {
    throw "No release archives (tm7-*.zip / tm7-*.tar.gz) were found under '$inputDirectory'."
}

if ($ExpectedAssetCount -gt 0 -and $archives.Count -ne $ExpectedAssetCount) {
    $names = ($archives | ForEach-Object { $_.Name }) -join ', '
    throw "Expected $ExpectedAssetCount release archive(s) but found $($archives.Count): $names"
}

$assets = @()
$checksumLines = @()

foreach ($archive in $archives) {
    $destination = Join-Path $outputDirectory $archive.Name
    Copy-Item $archive.FullName $destination -Force

    # Hash the copy in the output directory so checksums match the shipped bytes.
    $hash = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLines += "$hash  $($archive.Name)"

    $rid = $archive.Name -replace '\.zip$', '' -replace '\.tar\.gz$', '' -replace '^tm7-', ''
    $ridParts = $rid.Split('-', 2)
    $platform = $ridParts[0]
    $architecture = if ($ridParts.Length -gt 1) { $ridParts[1] } else { '' }
    $fileType = if ($archive.Name.EndsWith('.zip')) { 'zip' } else { 'tar.gz' }

    $assets += [ordered]@{
        name              = $archive.Name
        runtimeIdentifier = $rid
        platform          = $platform
        architecture      = $architecture
        fileType          = $fileType
        commandName       = 'tm7'
        sha256            = $hash
    }
}

$checksumLines | Set-Content -Path (Join-Path $outputDirectory 'checksums.txt') -Encoding ascii

$metadata = [ordered]@{
    version             = $Version
    runtimeDependencies = @('Graphviz dot executable for layout-backed commands')
    assets              = $assets
}
$metadata | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outputDirectory 'release-metadata.json') -Encoding utf8

Write-Host "Bundled $($archives.Count) archive(s) for version $Version into '$outputDirectory'."
