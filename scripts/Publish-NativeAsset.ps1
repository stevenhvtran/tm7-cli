<#
.SYNOPSIS
    Publishes the tm7 CLI as a self-contained NativeAOT binary for a single
    runtime identifier and packages it into a release archive.

.DESCRIPTION
    Produces, under the artifacts directory:
      * tm7-<rid>.zip      (Windows) or tm7-<rid>.tar.gz (Linux/macOS)
      * tm7-<rid>.json     sidecar metadata describing the archive (incl. sha256)

    tm7 is a pure-managed NativeAOT application (no bundled native sidecar
    libraries), so the published output is a single executable. Graphviz remains
    an external runtime prerequisite for layout-backed commands. The archive
    contains the executable plus any user-facing docs found at the repo root.

.NOTES
    Designed to run on the *native* architecture for the requested RID, so no
    cross-compilation toolchain is required. Cross-platform: runs under pwsh 7+
    on Windows, Linux, and macOS.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [string]$ProjectPath = [System.IO.Path]::Combine($PSScriptRoot, '..', 'src', 'Tm7.Cli', 'Tm7.Cli.csproj'),

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$artifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$repoRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..'))

$parts = $RuntimeIdentifier.Split('-', 2)
if ($parts.Length -ne 2) {
    throw "Unexpected runtime identifier format '$RuntimeIdentifier'. Expected '<platform>-<architecture>'."
}
$platform = $parts[0]
$architecture = $parts[1]

$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('tm7-publish-' + [System.Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $publishRoot 'publish'
$stagingDirectory = Join-Path $publishRoot 'stage'

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

try {
    $publishArguments = @(
        'publish', $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--nologo',
        "-p:Version=$Version",
        '-p:ContinuousIntegrationBuild=true',
        '-o', $publishDirectory
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime '$RuntimeIdentifier'. Ensure the NativeAOT toolchain is available for this platform."
    }

    $binaryName = if ($platform -eq 'win') { 'tm7.exe' } else { 'tm7' }
    $binaryPath = Join-Path $publishDirectory $binaryName
    if (-not (Test-Path $binaryPath)) {
        throw "Published binary '$binaryPath' was not found."
    }

    # Stage the single self-contained native binary plus any user-facing docs.
    Copy-Item $binaryPath (Join-Path $stagingDirectory $binaryName) -Force

    foreach ($doc in @('README.md', 'LICENSE', 'LICENSE.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md')) {
        $docPath = Join-Path $repoRoot $doc
        if (Test-Path $docPath) {
            Copy-Item $docPath (Join-Path $stagingDirectory $doc) -Force
        }
    }

    # Preserve the executable bit so extracted Unix binaries are runnable directly.
    if ($platform -ne 'win') {
        & chmod '+x' (Join-Path $stagingDirectory $binaryName)
    }

    if ($platform -eq 'win') {
        $assetPath = Join-Path $artifactsDirectory "tm7-$RuntimeIdentifier.zip"
        $fileType = 'zip'
        if (Test-Path $assetPath) { Remove-Item $assetPath -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDirectory, $assetPath)
    }
    else {
        $assetPath = Join-Path $artifactsDirectory "tm7-$RuntimeIdentifier.tar.gz"
        $fileType = 'tar.gz'
        if (Test-Path $assetPath) { Remove-Item $assetPath -Force }
        tar -czf $assetPath -C $stagingDirectory .
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create archive '$assetPath'."
        }
    }

    $assetName = [System.IO.Path]::GetFileName($assetPath)
    $hash = (Get-FileHash $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $metadata = [ordered]@{
        version           = $Version
        runtimeIdentifier = $RuntimeIdentifier
        platform          = $platform
        architecture      = $architecture
        assetName         = $assetName
        fileType          = $fileType
        commandName       = 'tm7'
        runtimeDependencies = @('Graphviz dot executable for layout-backed commands')
        sha256            = $hash
    }
    $metadataPath = Join-Path $artifactsDirectory "tm7-$RuntimeIdentifier.json"
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath -Encoding utf8

    Write-Host "Created $assetName ($fileType, sha256=$hash)"
}
finally {
    if (Test-Path $publishRoot) {
        Remove-Item $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
