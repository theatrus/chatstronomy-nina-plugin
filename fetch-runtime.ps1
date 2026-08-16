[CmdletBinding()]
param(
    [string] $LockPath = 'runtime.lock.json',
    [string] $CacheDirectory = 'runtime-cache'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = $PSScriptRoot
$resolvedLockPath = if ([IO.Path]::IsPathRooted($LockPath)) {
    $LockPath
} else {
    Join-Path $repositoryRoot $LockPath
}
$resolvedCache = if ([IO.Path]::IsPathRooted($CacheDirectory)) {
    $CacheDirectory
} else {
    Join-Path $repositoryRoot $CacheDirectory
}

if (-not (Test-Path -LiteralPath $resolvedLockPath -PathType Leaf)) {
    throw "Runtime lock was not found at $resolvedLockPath."
}

$lock = Get-Content -LiteralPath $resolvedLockPath -Raw | ConvertFrom-Json
if ($lock.schema_version -ne 1) {
    throw "Unsupported runtime lock schema $($lock.schema_version)."
}
if ($lock.state -ne 'locked') {
    throw "runtime.lock.json is '$($lock.state)'. Merge the backend artifact PR, publish its pinned release, and record the manifest SHA-256 before fetching."
}

$backendRepository = [string] $lock.backend.repository
$backendTag = [string] $lock.backend.tag
$expectedManifestHash = [string] $lock.backend.runtime_manifest_sha256
if ($backendRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    $backendTag -notmatch '^v\d+\.\d+\.\d+$' -or
    $expectedManifestHash -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'runtime.lock.json does not contain a complete immutable backend release pin.'
}

$releaseBase = "https://github.com/$backendRepository/releases/download/$backendTag"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "chatstronomy-runtime-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

function Assert-Asset([string] $Path, $Asset, [string] $Description) {
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $actualHash.Equals([string] $Asset.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description checksum mismatch. Expected $($Asset.sha256), got $actualHash."
    }
    $actualSize = (Get-Item -LiteralPath $Path).Length
    if ($actualSize -ne [long] $Asset.size) {
        throw "$Description size mismatch. Expected $($Asset.size), got $actualSize."
    }
}

function Receive-Asset($Manifest, [string] $Key, [string] $Description) {
    $assetProperty = $Manifest.assets.PSObject.Properties[$Key]
    if ($null -eq $assetProperty) {
        throw "Runtime manifest does not define asset key '$Key'."
    }
    $asset = $assetProperty.Value
    $name = [string] $asset.name
    if ([IO.Path]::GetFileName($name) -ne $name) {
        throw "Runtime manifest contains unsafe asset name '$name'."
    }
    $destination = Join-Path $temporaryRoot $name
    Invoke-WebRequest -UseBasicParsing -Uri "$releaseBase/$([Uri]::EscapeDataString($name))" -OutFile $destination
    Assert-Asset $destination $asset $Description
    return [pscustomobject]@{ Path = $destination; Asset = $asset }
}

try {
    $manifestPath = Join-Path $temporaryRoot 'chatstronomy-runtime-manifest.json'
    Invoke-WebRequest -UseBasicParsing -Uri "$releaseBase/chatstronomy-runtime-manifest.json" -OutFile $manifestPath
    $actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if (-not $actualManifestHash.Equals($expectedManifestHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime manifest checksum mismatch. Expected $expectedManifestHash, got $actualManifestHash."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema_version -ne 1 -or
        $manifest.release.repository -ne $backendRepository -or
        $manifest.release.tag -ne $backendTag) {
        throw 'Runtime manifest identity does not match runtime.lock.json.'
    }
    if ($manifest.protocols.direct.version -ne $lock.protocols.direct -or
        $manifest.protocols.plugin_runtime.version -ne $lock.protocols.plugin_runtime) {
        throw 'Runtime manifest protocol versions do not match runtime.lock.json.'
    }

    $pluginRuntime = Receive-Asset $manifest ([string] $lock.asset_keys.plugin_runtime) 'Plugin runtime'
    $fullRuntime = Receive-Asset $manifest ([string] $lock.asset_keys.full_runtime) 'Full runtime'
    $contracts = Receive-Asset $manifest ([string] $lock.asset_keys.contracts) 'Plugin contracts'

    $tagCache = Join-Path $resolvedCache $backendTag
    New-Item -ItemType Directory -Path $tagCache -Force | Out-Null
    Copy-Item -LiteralPath $pluginRuntime.Path -Destination (Join-Path $tagCache 'chatstronomy.exe') -Force
    Copy-Item -LiteralPath $fullRuntime.Path -Destination (Join-Path $tagCache 'chatstronomy-hub-probe.exe') -Force
    Copy-Item -LiteralPath $contracts.Path -Destination (Join-Path $tagCache 'chatstronomy-plugin-contracts-v1.zip') -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $tagCache 'chatstronomy-runtime-manifest.json') -Force

    $contractsDestination = Join-Path $tagCache 'contracts-expanded'
    if (Test-Path -LiteralPath $contractsDestination) {
        $cachePrefix = [IO.Path]::GetFullPath($resolvedCache).TrimEnd('\') + '\'
        $contractsFullPath = [IO.Path]::GetFullPath($contractsDestination)
        if (-not $contractsFullPath.StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace contracts outside runtime cache: $contractsFullPath"
        }
        Remove-Item -LiteralPath $contractsDestination -Recurse -Force
    }
    Expand-Archive -LiteralPath $contracts.Path -DestinationPath $contractsDestination

    New-Item -ItemType Directory -Path $resolvedCache -Force | Out-Null
    Copy-Item -LiteralPath $pluginRuntime.Path -Destination (Join-Path $resolvedCache 'chatstronomy.exe') -Force
    Copy-Item -LiteralPath $fullRuntime.Path -Destination (Join-Path $resolvedCache 'chatstronomy-hub-probe.exe') -Force

    [pscustomobject]@{
        Tag = $backendTag
        Runtime = Join-Path $resolvedCache 'chatstronomy.exe'
        HubProbe = Join-Path $resolvedCache 'chatstronomy-hub-probe.exe'
        Contracts = Join-Path $contractsDestination 'contracts'
        Manifest = Join-Path $tagCache 'chatstronomy-runtime-manifest.json'
    }
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
