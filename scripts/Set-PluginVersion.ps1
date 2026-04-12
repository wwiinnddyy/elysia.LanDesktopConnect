[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepositoryRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
}

function Assert-Version([string]$Value) {
    $candidate = $Value.Trim()
    if ($candidate.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(1)
    }

    $core = ($candidate -split '[-+ ]', 2)[0]
    $parsed = $null
    if (-not [Version]::TryParse($core, [ref]$parsed)) {
        throw "Invalid plugin version '$Value'."
    }

    return $candidate
}

function Write-Utf8File([string]$Path, [string]$Content) {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

$normalizedVersion = Assert-Version $Version
$pluginId = "Elysia.LanDesktopConnect"
$assetName = "$pluginId.$normalizedVersion.laapp"
$releaseTag = "v$normalizedVersion"

$csprojPath = Join-Path $RepositoryRoot "Elysia.LanDesktopConnect.csproj"
$manifestPath = Join-Path $RepositoryRoot "plugin.json"
$readmePath = Join-Path $RepositoryRoot "README.md"

$csprojContent = [System.IO.File]::ReadAllText($csprojPath)
$versionPattern = "<Version>.*?</Version>"
if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
    $csprojContent,
    $versionPattern,
    [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
    throw "Failed to locate <Version> in '$csprojPath'."
}
$updatedCsproj = [System.Text.RegularExpressions.Regex]::Replace(
    $csprojContent,
    $versionPattern,
    "<Version>$normalizedVersion</Version>",
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
Write-Utf8File -Path $csprojPath -Content $updatedCsproj

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $normalizedVersion
$manifestJson = $manifest | ConvertTo-Json -Depth 10
Write-Utf8File -Path $manifestPath -Content ($manifestJson + [Environment]::NewLine)

Write-Host "Updated plugin version to $normalizedVersion."
