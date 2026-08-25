[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $Version = '0.1.0.23',

    [ValidateNotNullOrEmpty()]
    [string] $InstallerUrl = '',

    [ValidateNotNullOrEmpty()]
    [string] $FeaturedImageUrl = 'https://raw.githubusercontent.com/theatrus/chatstronomy-nina-plugin/main/assets/branding/chatstronomy-featured.png',

    [ValidateNotNullOrEmpty()]
    [string] $OutputDirectory = 'artifacts/nina-plugin',

    [string] $RuntimePath = 'runtime-cache/chatstronomy.exe',

    [switch] $SkipRuntime,

    # Authenticode signing must happen after the package is staged but before
    # its archive checksum is written to the N.I.N.A. manifest.
    [switch] $StageOnly,

    [switch] $PackageOnly
)

if ($StageOnly -and $PackageOnly) {
    throw 'Specify at most one of -StageOnly and -PackageOnly.'
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = $PSScriptRoot
$project = Join-Path $repositoryRoot 'Chatstronomy.NINA/Chatstronomy.NINA.csproj'
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repositoryRoot $OutputDirectory
}

$archiveName = "Chatstronomy.NINA.$Version.zip"
if ([string]::IsNullOrWhiteSpace($InstallerUrl)) {
    $InstallerUrl = "https://github.com/theatrus/chatstronomy-nina-plugin/releases/download/v$Version/$archiveName"
}

$parsedInstallerUrl = $null
if (-not [Uri]::TryCreate($InstallerUrl, [UriKind]::Absolute, [ref] $parsedInstallerUrl) -or
    $parsedInstallerUrl.Scheme -notin @('http', 'https')) {
    throw 'InstallerUrl must be an absolute http:// or https:// URL.'
}

$parsedFeaturedImageUrl = $null
if (-not [Uri]::TryCreate($FeaturedImageUrl, [UriKind]::Absolute, [ref] $parsedFeaturedImageUrl) -or
    $parsedFeaturedImageUrl.Scheme -notin @('http', 'https')) {
    throw 'FeaturedImageUrl must be an absolute http:// or https:// URL.'
}

$buildDirectory = Join-Path $outputRoot 'build'
$packageDirectory = Join-Path $outputRoot 'package/Chatstronomy'
$archivePath = Join-Path $outputRoot $archiveName
$manifestPath = Join-Path $outputRoot "Chatstronomy.NINA.$Version.manifest.json"

if (-not $PackageOnly) {
    foreach ($directory in @($buildDirectory, $packageDirectory)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
} elseif (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    throw "-PackageOnly needs a staged package at $packageDirectory; run -StageOnly first."
}

foreach ($file in @($archivePath, $manifestPath)) {
    if (Test-Path -LiteralPath $file) {
        Remove-Item -LiteralPath $file -Force
    }
}

$packagedRuntime = $null
$pluginDll = Join-Path $packageDirectory 'Chatstronomy.dll'
$pluginLicense = Join-Path $packageDirectory 'LICENSE'
$thirdPartyNotices = Join-Path $packageDirectory 'THIRD-PARTY-NOTICES.md'
$runtimeFontLicense = Join-Path $packageDirectory 'runtime/LiberationSans-LICENSE'

if (-not $PackageOnly) {
    dotnet build $project `
        --configuration Release `
        --output $buildDirectory `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "Chatstronomy N.I.N.A. plugin build failed with exit code $LASTEXITCODE."
    }

    $builtPluginDll = Join-Path $buildDirectory 'Chatstronomy.dll'
    if (-not (Test-Path -LiteralPath $builtPluginDll -PathType Leaf)) {
        throw "Expected plugin assembly was not produced at $builtPluginDll."
    }

    Copy-Item -LiteralPath $builtPluginDll -Destination $pluginDll
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $pluginLicense
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $thirdPartyNotices

    if (-not $SkipRuntime) {
        if ([string]::IsNullOrWhiteSpace($RuntimePath)) {
            throw 'RuntimePath is required unless -SkipRuntime is set.'
        }
        if (-not [IO.Path]::IsPathRooted($RuntimePath)) {
            $RuntimePath = Join-Path $repositoryRoot $RuntimePath
        }

        if (-not (Test-Path -LiteralPath $RuntimePath -PathType Leaf)) {
            throw "Expected the pinned Chatstronomy runtime at $RuntimePath. Run ./fetch-runtime.ps1 or pass -RuntimePath explicitly."
        }
        $runtimeDirectory = Join-Path $packageDirectory 'runtime'
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        $packagedRuntime = Join-Path $runtimeDirectory 'chatstronomy.exe'
        Copy-Item -LiteralPath $RuntimePath -Destination $packagedRuntime
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses/LiberationSans-LICENSE') -Destination $runtimeFontLicense
    }

} else {
    if (-not (Test-Path -LiteralPath $pluginDll -PathType Leaf)) {
        throw "The staged package does not contain $pluginDll."
    }
    if (-not $SkipRuntime) {
        $packagedRuntime = Join-Path $packageDirectory 'runtime/chatstronomy.exe'
        if (-not (Test-Path -LiteralPath $packagedRuntime -PathType Leaf)) {
            throw "The staged package does not contain $packagedRuntime."
        }
    }
}

foreach ($notice in @($pluginLicense, $thirdPartyNotices)) {
    if (-not (Test-Path -LiteralPath $notice -PathType Leaf)) {
        throw "The staged package does not contain required redistribution notice $notice."
    }
}

$stagedRuntime = Join-Path $packageDirectory 'runtime/chatstronomy.exe'
if (Test-Path -LiteralPath $stagedRuntime -PathType Leaf) {
    if ($SkipRuntime) {
        throw "-SkipRuntime cannot package a staged runtime at $stagedRuntime."
    }
    if (-not (Test-Path -LiteralPath $runtimeFontLicense -PathType Leaf)) {
        throw "The staged runtime does not contain its required Liberation Sans license at $runtimeFontLicense."
    }
}

if ($StageOnly) {
    [pscustomobject]@{
        Package = $packageDirectory
        Plugin = $pluginDll
        Runtime = $packagedRuntime
    }
    return
}

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath

$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$versionParts = $Version.Split('.')
$manifest = [ordered]@{
    Name = 'Chatstronomy'
    Identifier = '5e7c25c4-f654-4e22-9e21-3127048221c0'
    Version = [ordered]@{
        Major = $versionParts[0]
        Minor = $versionParts[1]
        Patch = $versionParts[2]
        Build = $versionParts[3]
    }
    Author = 'Yann Ramin'
    Homepage = 'https://chatstronomy.com/'
    Repository = 'https://github.com/theatrus/chatstronomy-nina-plugin'
    License = 'Apache-2.0'
    LicenseURL = 'https://github.com/theatrus/chatstronomy-nina-plugin/blob/main/LICENSE'
    ChangelogURL = 'https://github.com/theatrus/chatstronomy-nina-plugin/releases'
    Tags = @('discord', 'matrix', 'monitoring', 'remote')
    MinimumApplicationVersion = [ordered]@{
        Major = '3'
        Minor = '2'
        Patch = '0'
        Build = '9001'
    }
    Descriptions = [ordered]@{
        ShortDescription = 'Bridge NINA with Discord and Matrix, supporting bot slash commands for control'
        LongDescription = 'Routes native N.I.N.A. status, events, images, popup notifications, selected logs, and approved commands through Discord and Matrix. Supports a supervised local runtime or multi-system Chatstronomy Hub.'
        FeaturedImageURL = $FeaturedImageUrl
        ScreenshotURL = ''
        AltScreenshotURL = ''
    }
    Installer = [ordered]@{
        URL = $InstallerUrl
        Type = 'ARCHIVE'
        Checksum = $checksum
        ChecksumType = 'SHA256'
    }
}

$json = $manifest | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Archive = $archivePath
    Manifest = $manifestPath
    Checksum = $checksum
    InstallerUrl = $InstallerUrl
    Runtime = $packagedRuntime
}
