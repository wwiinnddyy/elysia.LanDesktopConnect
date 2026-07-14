[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$PackagePath,
    [string]$MarketManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepositoryRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
}

function Assert-Equal($Actual, $Expected, [string]$FieldName) {
    if ([string]$Actual -ne [string]$Expected) {
        throw "$FieldName mismatch. Expected '$Expected', actual '$Actual'."
    }
}

function Read-PackageManifest([System.IO.Compression.ZipArchive]$Archive) {
    $entries = @($Archive.Entries | Where-Object { $_.FullName -eq "plugin.json" })
    if ($entries.Count -ne 1) {
        throw "Package must contain exactly one root plugin.json."
    }

    $stream = $entries[0].Open()
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::UTF8, $true)
    try {
        return $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$projectPath = Join-Path $RepositoryRoot "Elysia.LanDesktopConnect.csproj"
$manifestPath = Join-Path $RepositoryRoot "plugin.json"
$marketTemplatePath = Join-Path $RepositoryRoot "airappmarket-entry.template.json"

[xml]$project = Get-Content -LiteralPath $projectPath -Encoding UTF8 -Raw
$versionNode = $project.SelectSingleNode("/Project/PropertyGroup/Version")
if ($null -eq $versionNode) { throw "Project is missing <Version>." }
$projectVersion = [string]$versionNode.InnerText

$manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
Assert-Equal $manifest.id "Elysia.LanDesktopConnect" "plugin id"
Assert-Equal $manifest.version $projectVersion "plugin version"
Assert-Equal $manifest.apiVersion "5.0.0" "PluginSdk API version"
Assert-Equal $manifest.entranceAssembly "Elysia.LanDesktopConnect.dll" "entrance assembly"
Assert-Equal $manifest.runtime.mode "in-proc" "runtime mode"

if ([Version]$projectVersion -le [Version]"0.0.2") {
    throw "The API 5 release must not overwrite published/current 0.0.1 or 0.0.2 packages."
}

$packageReferences = @{}
foreach ($reference in $project.SelectNodes("/Project/ItemGroup/PackageReference")) {
    $packageReferences[[string]$reference.Include] = [string]$reference.Version
}

$requiredReferences = [ordered]@{
    "Avalonia" = "12.1.0"
    "FluentAvaloniaUI" = "3.0.1"
    "FluentIcons.Avalonia" = "2.1.331"
    "LanMountainDesktop.PluginSdk" = "5.0.0"
}

foreach ($packageId in $requiredReferences.Keys) {
    if (-not $packageReferences.ContainsKey($packageId)) {
        throw "Missing required package reference '$packageId'."
    }
    Assert-Equal $packageReferences[$packageId] $requiredReferences[$packageId] "$packageId version"
}

$assetsPath = Join-Path $RepositoryRoot "obj\project.assets.json"
if (Test-Path -LiteralPath $assetsPath -PathType Leaf) {
    $assets = Get-Content -LiteralPath $assetsPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $resolvedLibraries = @($assets.libraries.PSObject.Properties.Name)
    foreach ($packageId in $requiredReferences.Keys) {
        $expectedLibrary = "$packageId/$($requiredReferences[$packageId])"
        if ($resolvedLibraries -notcontains $expectedLibrary) {
            throw "project.assets.json did not resolve '$expectedLibrary'. Run restore with --force --no-cache."
        }
    }
}

$template = Get-Content -LiteralPath $marketTemplatePath -Encoding UTF8 -Raw | ConvertFrom-Json
Assert-Equal $template.minHostVersion "0.8.6" "market minHostVersion"
if (@($template.desktopComponents) -notcontains "Elysia.LanDesktopConnect.GatewayStatus") {
    throw "Market capabilities must declare the API 5 gateway status component."
}
if (@($template.settingsSections) -notcontains "IpcBridgeSettingsSection") {
    throw "Market capabilities must declare IpcBridgeSettingsSection."
}

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
    $expectedAssetName = "$($manifest.id).$projectVersion.laapp"
    Assert-Equal ([System.IO.Path]::GetFileName($resolvedPackagePath)) $expectedAssetName "package asset name"

    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        $requiredEntries = @(
            "plugin.json",
            "Elysia.LanDesktopConnect.dll",
            "Elysia.LanDesktopConnect.deps.json",
            "Localization/en-US.json",
            "Localization/zh-CN.json",
            "assets/elysia.svg",
            "elysia-gateway/package.json",
            "elysia-gateway/bun.lock",
            "elysia-gateway/src/index.ts"
        )

        foreach ($entryName in $requiredEntries) {
            if ($entryNames -notcontains $entryName) {
                throw "Package is missing required entry '$entryName'."
            }
        }

        $forbiddenPatterns = @(
            '\.pdb$',
            '(^|/)node_modules/',
            '(^|/)obj/',
            '(^|/)bin/',
            '^LanMountainDesktop\.',
            '^Avalonia',
            '^FluentAvalonia',
            '^FluentIcons'
        )
        foreach ($entryName in $entryNames) {
            if ($entryName.Contains("..")) {
                throw "Package contains unsafe entry '$entryName'."
            }
            foreach ($pattern in $forbiddenPatterns) {
                if ($entryName -match $pattern) {
                    throw "Package contains forbidden host/development asset '$entryName'."
                }
            }
        }

        $packageManifest = Read-PackageManifest -Archive $archive
        Assert-Equal $packageManifest.id $manifest.id "package plugin id"
        Assert-Equal $packageManifest.version $manifest.version "package plugin version"
        Assert-Equal $packageManifest.apiVersion $manifest.apiVersion "package API version"
        Assert-Equal $packageManifest.runtime.mode "in-proc" "package runtime mode"
    }
    finally {
        $archive.Dispose()
    }
}

if (-not [string]::IsNullOrWhiteSpace($MarketManifestPath)) {
    $market = Get-Content -LiteralPath $MarketManifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    Assert-Equal $market.schemaVersion "2.0.0" "market schemaVersion"
    Assert-Equal $market.manifest.id $manifest.id "market plugin id"
    Assert-Equal $market.manifest.version $manifest.version "market plugin version"
    Assert-Equal $market.manifest.apiVersion "5.0.0" "market API version"
    Assert-Equal $market.compatibility.minHostVersion "0.8.6" "market compatibility minHostVersion"
    Assert-Equal $market.publication.releaseTag "v$projectVersion" "market release tag"
    Assert-Equal $market.publication.releaseAssetName "$($manifest.id).$projectVersion.laapp" "market release asset"

    $sources = @($market.publication.packageSources)
    Assert-Equal $sources.Count 3 "market package source count"
    Assert-Equal $sources[0].kind "releaseAsset" "first package source"
    Assert-Equal $sources[1].kind "rawFallback" "second package source"
    Assert-Equal $sources[2].kind "workspaceLocal" "third package source"
    if ([string]::IsNullOrWhiteSpace([string]$sources[2].url) -or
        -not ([string]$sources[2].url).StartsWith("workspace://", [System.StringComparison]::Ordinal)) {
        throw "workspaceLocal must use a workspace:// URL."
    }
}

Write-Host "Plugin version: $projectVersion"
Write-Host "Plugin API version: $($manifest.apiVersion)"
Write-Host "Minimum host version: $($template.minHostVersion)"
Write-Host "Expected asset: $($manifest.id).$projectVersion.laapp"
