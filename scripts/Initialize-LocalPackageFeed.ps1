[CmdletBinding()]
param(
    [string]$FeedPath,
    [string]$PluginSdkProjectPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($FeedPath)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $FeedPath = Join-Path (Resolve-Path (Join-Path $scriptRoot "..")).Path "packages"
}

if ([string]::IsNullOrWhiteSpace($PluginSdkProjectPath)) {
    $PluginSdkProjectPath = (Resolve-Path "..\LanMountainDesktop\LanMountainDesktop.PluginSdk\LanMountainDesktop.PluginSdk.csproj").Path
}

function Pack-Project([string]$ProjectPath, [string]$OutputDirectory) {
    if (-not (Test-Path $ProjectPath)) {
        throw "Project '$ProjectPath' was not found."
    }

    dotnet pack $ProjectPath -c Release -o $OutputDirectory -p:ContinuousIntegrationBuild=true | Out-Host
}

New-Item -ItemType Directory -Force -Path $FeedPath | Out-Null
Pack-Project -ProjectPath $PluginSdkProjectPath -OutputDirectory $FeedPath

Write-Host "Local package feed initialized at '$FeedPath'."
