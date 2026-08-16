[CmdletBinding()]
param(
    [string] $RegistryRoot = '../spacecat/artifacts/local-registry',

    [string] $RuntimePath = 'runtime-cache/chatstronomy.exe',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0.17',

    [ValidatePattern('^https?://')]
    [string] $RegistryBaseUrl = 'http://127.0.0.1:8765'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedRegistryRoot = if ([IO.Path]::IsPathRooted($RegistryRoot)) {
    $RegistryRoot
} else {
    Join-Path $repositoryRoot $RegistryRoot
}
$resolvedRegistryRoot = [IO.Path]::GetFullPath($resolvedRegistryRoot)

$manifestEndpoint = Join-Path $resolvedRegistryRoot 'plugins/manifests'
if (-not (Test-Path -LiteralPath $manifestEndpoint -PathType Leaf)) {
    throw "N.I.N.A. registry manifest endpoint was not found at $manifestEndpoint."
}

$packagesDirectory = Join-Path $resolvedRegistryRoot 'packages'
$imagesDirectory = Join-Path $resolvedRegistryRoot 'images'
New-Item -ItemType Directory -Path $packagesDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $imagesDirectory -Force | Out-Null

$archiveName = "Chatstronomy.NINA.$Version.zip"
$imageName = "chatstronomy-featured-$Version.png"
$installerUrl = "$($RegistryBaseUrl.TrimEnd('/'))/packages/$archiveName"
$featuredImageUrl = "$($RegistryBaseUrl.TrimEnd('/'))/images/$imageName"
$outputDirectory = Join-Path $repositoryRoot 'artifacts/local-publish'

$package = & (Join-Path $repositoryRoot 'build-package.ps1') `
    -Version $Version `
    -RuntimePath $RuntimePath `
    -InstallerUrl $installerUrl `
    -FeaturedImageUrl $featuredImageUrl `
    -OutputDirectory $outputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Plugin packaging failed with exit code $LASTEXITCODE."
}

$archivePath = Join-Path $outputDirectory $archiveName
$generatedManifestPath = Join-Path $outputDirectory "Chatstronomy.NINA.$Version.manifest.json"
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $generatedManifestPath -PathType Leaf)) {
    throw 'Plugin packaging did not produce the expected archive and manifest.'
}

Copy-Item -LiteralPath $archivePath -Destination (Join-Path $packagesDirectory $archiveName) -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'assets/branding/chatstronomy-featured.png') `
    -Destination (Join-Path $imagesDirectory $imageName) -Force

$parsedExisting = Get-Content -LiteralPath $manifestEndpoint -Raw | ConvertFrom-Json
$existing = @()
foreach ($entry in $parsedExisting) {
    $identifierProperty = $entry.PSObject.Properties['Identifier']
    $wrappedValueProperty = $entry.PSObject.Properties['value']
    if ($null -ne $identifierProperty) {
        $existing += $entry
    } elseif ($null -ne $wrappedValueProperty) {
        foreach ($wrappedEntry in $wrappedValueProperty.Value) {
            if ($null -eq $wrappedEntry.PSObject.Properties['Identifier']) {
                throw 'Local registry contains an unrecognized wrapped manifest entry.'
            }
            $existing += $wrappedEntry
        }
    } else {
        throw 'Local registry contains an unrecognized manifest entry.'
    }
}
$generated = Get-Content -LiteralPath $generatedManifestPath -Raw | ConvertFrom-Json
$pluginIdentifier = '5e7c25c4-f654-4e22-9e21-3127048221c0'
$updated = @($existing | Where-Object { $_.Identifier -ne $pluginIdentifier }) + @($generated)
$json = $updated | ConvertTo-Json -Depth 20 -Compress

$temporaryManifest = Join-Path (Split-Path -Parent $manifestEndpoint) ".manifests-$([Guid]::NewGuid().ToString('N')).tmp"
$backupManifest = Join-Path (Split-Path -Parent $manifestEndpoint) ".manifests-$([Guid]::NewGuid().ToString('N')).bak"
[IO.File]::WriteAllText($temporaryManifest, $json, [Text.UTF8Encoding]::new($false))
try {
    [IO.File]::Replace($temporaryManifest, $manifestEndpoint, $backupManifest)
    Remove-Item -LiteralPath $backupManifest -Force
} finally {
    if (Test-Path -LiteralPath $temporaryManifest) {
        Remove-Item -LiteralPath $temporaryManifest -Force
    }
    if (Test-Path -LiteralPath $backupManifest) {
        Remove-Item -LiteralPath $backupManifest -Force
    }
}

[pscustomobject]@{
    Version = $Version
    Registry = $resolvedRegistryRoot
    Archive = Join-Path $packagesDirectory $archiveName
    ManifestEndpoint = $manifestEndpoint
    PreservedPlugins = @($updated | Where-Object { $_.Identifier -ne $pluginIdentifier }).Count
    InstallerUrl = $installerUrl
}
