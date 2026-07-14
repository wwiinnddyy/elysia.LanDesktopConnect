[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepositoryRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
}

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}

if ($normalizedVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Plugin version must be a three-part version, actual '$Version'."
}

if ([Version]$normalizedVersion -le [Version]"0.0.2") {
    throw "Version '$normalizedVersion' would overwrite the published/current 0.0.1 or 0.0.2 line."
}

function Write-Utf8File([string]$Path, [string]$Content) {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

$projectPath = Join-Path $RepositoryRoot "Elysia.LanDesktopConnect.csproj"
$manifestPath = Join-Path $RepositoryRoot "plugin.json"
$gatewayPackagePath = Join-Path $RepositoryRoot "elysia-gateway\package.json"

$projectContent = [System.IO.File]::ReadAllText($projectPath)
if ($projectContent -notmatch '<Version>[^<]+</Version>') {
    throw "Failed to locate <Version> in '$projectPath'."
}
$projectContent = [System.Text.RegularExpressions.Regex]::Replace(
    $projectContent,
    '<Version>[^<]+</Version>',
    "<Version>$normalizedVersion</Version>",
    1)
Write-Utf8File -Path $projectPath -Content $projectContent

$manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
$manifest.version = $normalizedVersion
Write-Utf8File -Path $manifestPath -Content (($manifest | ConvertTo-Json -Depth 20) + [Environment]::NewLine)

$gatewayPackage = Get-Content -LiteralPath $gatewayPackagePath -Encoding UTF8 -Raw | ConvertFrom-Json
$gatewayPackage.version = $normalizedVersion
Write-Utf8File -Path $gatewayPackagePath -Content (($gatewayPackage | ConvertTo-Json -Depth 20) + [Environment]::NewLine)

Write-Host "Updated plugin and gateway versions to $normalizedVersion."
Write-Host "Run 'bun install' in elysia-gateway, rebuild the .laapp, and regenerate market-manifest.json before release."
