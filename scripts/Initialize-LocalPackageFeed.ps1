[CmdletBinding()]
param(
    [string]$FeedPath,
    [string]$LanMountainDesktopRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($FeedPath)) {
    $FeedPath = Join-Path $repositoryRoot "packages"
}

if ([string]::IsNullOrWhiteSpace($LanMountainDesktopRoot)) {
    $LanMountainDesktopRoot = (Resolve-Path (Join-Path $repositoryRoot "..\LanMountainDesktop")).Path
}
else {
    $LanMountainDesktopRoot = (Resolve-Path $LanMountainDesktopRoot).Path
}

$projects = @(
    "LanMountainDesktop.Shared.Contracts\LanMountainDesktop.Shared.Contracts.csproj",
    "LanMountainDesktop.PluginIsolation.Contracts\LanMountainDesktop.PluginIsolation.Contracts.csproj",
    "LanMountainDesktop.Shared.IPC\LanMountainDesktop.Shared.IPC.csproj",
    "LanMountainDesktop.PluginSdk\LanMountainDesktop.PluginSdk.csproj"
)

New-Item -ItemType Directory -Force -Path $FeedPath | Out-Null

foreach ($relativeProjectPath in $projects) {
    $projectPath = Join-Path $LanMountainDesktopRoot $relativeProjectPath
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Required PluginSdk 5 project '$projectPath' was not found."
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Encoding UTF8 -Raw
    $version = [string]$projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ($version -ne "5.0.0") {
        throw "Project '$projectPath' must declare version 5.0.0, actual '$version'."
    }

    dotnet pack $projectPath `
        -c Release `
        -o $FeedPath `
        -p:ContinuousIntegrationBuild=true `
        -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for '$projectPath' with exit code $LASTEXITCODE."
    }
}

$requiredPackages = @(
    "LanMountainDesktop.Shared.Contracts.5.0.0.nupkg",
    "LanMountainDesktop.PluginIsolation.Contracts.5.0.0.nupkg",
    "LanMountainDesktop.Shared.IPC.5.0.0.nupkg",
    "LanMountainDesktop.PluginSdk.5.0.0.nupkg"
)

foreach ($packageName in $requiredPackages) {
    $packagePath = Join-Path $FeedPath $packageName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected local SDK package '$packagePath' was not generated."
    }
}

$repositoryPackageCache = Join-Path $repositoryRoot ".nuget\packages"
if (Test-Path -LiteralPath $repositoryPackageCache -PathType Container) {
    $cachedLanMountainPackages = Get-ChildItem -LiteralPath $repositoryPackageCache -Directory |
        Where-Object { $_.Name -like "lanmountaindesktop.*" }
    foreach ($cachedPackage in $cachedLanMountainPackages) {
        $resolvedCachePath = [System.IO.Path]::GetFullPath($cachedPackage.FullName)
        $resolvedCacheRoot = [System.IO.Path]::GetFullPath($repositoryPackageCache) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedCachePath.StartsWith($resolvedCacheRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove package cache outside '$repositoryPackageCache'."
        }
        Remove-Item -LiteralPath $resolvedCachePath -Recurse -Force
    }
}

Write-Host "PluginSdk 5 local package feed initialized at '$FeedPath'."
