[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TemplatePath,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ReleaseTag,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Utf8File([string]$Path, [string]$Content) {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-PropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ArrayValue($Object, [string]$Name) {
    $value = Get-PropertyValue -Object $Object -Name $Name
    if ($null -eq $value) { return @() }
    if ($value -is [array]) { return $value }
    return @($value)
}

function Assert-ThreePartVersion([string]$Value, [string]$FieldName) {
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "$FieldName must be a three-part version, actual '$Value'."
    }
}

function Get-RepositoryInfo([string]$RepositoryUrl) {
    $uri = [Uri]$RepositoryUrl
    if ($uri.Scheme -ne "https" -or $uri.Host -ne "github.com") {
        throw "Repository URL '$RepositoryUrl' must use https://github.com."
    }

    $segments = $uri.AbsolutePath.Trim("/") -split "/"
    if ($segments.Length -ne 2) {
        throw "Repository URL '$RepositoryUrl' must point to a GitHub repository root."
    }

    return @{ Owner = $segments[0]; Name = $segments[1] }
}

function Get-PackageManifest([string]$ArchivePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -eq "plugin.json" })
        if ($entries.Count -ne 1) {
            throw "Plugin package '$ArchivePath' must contain exactly one root plugin.json."
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
    finally {
        $archive.Dispose()
    }
}

$template = Get-Content -LiteralPath $TemplatePath -Encoding UTF8 -Raw | ConvertFrom-Json
$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
$manifest = Get-PackageManifest -ArchivePath $resolvedPackagePath

Assert-ThreePartVersion -Value $Version -FieldName "Version"
Assert-ThreePartVersion -Value ([string]$manifest.version) -FieldName "plugin.json version"
Assert-ThreePartVersion -Value ([string]$manifest.apiVersion) -FieldName "plugin.json apiVersion"

if ([string]$manifest.version -ne $Version) {
    throw "Requested version '$Version' does not match package manifest version '$($manifest.version)'."
}

if ([string]$manifest.apiVersion -ne "5.0.0") {
    throw "Production market releases must target PluginSdk API 5.0.0, actual '$($manifest.apiVersion)'."
}

if ($ReleaseTag -ne "v$Version") {
    throw "Release tag '$ReleaseTag' must be 'v$Version'."
}

$assetName = [System.IO.Path]::GetFileName($resolvedPackagePath)
$expectedAssetName = "$($manifest.id).$Version.laapp"
if ($assetName -ne $expectedAssetName) {
    throw "Package name mismatch. Expected '$expectedAssetName', actual '$assetName'."
}

$repositoryUrl = [string](Get-PropertyValue $template "repositoryUrl")
$repo = Get-RepositoryInfo -RepositoryUrl $repositoryUrl
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY) -and $env:GITHUB_REPOSITORY -match '^[^/]+/[^/]+$') {
    $repositoryParts = $env:GITHUB_REPOSITORY -split '/', 2
    $repo.Owner = $repositoryParts[0]
    $repo.Name = $repositoryParts[1]
}

$actualRepositoryUrl = "https://github.com/$($repo.Owner)/$($repo.Name)"
$releaseDownloadUrl = "$actualRepositoryUrl/releases/download/$ReleaseTag/$assetName"
$rawFallbackUrl = "https://raw.githubusercontent.com/$($repo.Owner)/$($repo.Name)/main/$assetName"
$workspaceLocalUrl = "workspace://$($repo.Name)/$assetName"
$hash = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageSize = (Get-Item -LiteralPath $resolvedPackagePath).Length
$timestamp = [DateTimeOffset]::UtcNow.ToString("o")

$sharedContracts = @(Get-ArrayValue -Object $manifest -Name "sharedContracts")
$tags = @(Get-ArrayValue -Object $template -Name "tags")
$desktopComponents = @(Get-ArrayValue -Object $template -Name "desktopComponents")
$settingsSections = @(Get-ArrayValue -Object $template -Name "settingsSections")
$exports = @(Get-ArrayValue -Object $template -Name "exports")
$messageTypes = @(Get-ArrayValue -Object $template -Name "messageTypes")
$minHostVersion = [string](Get-PropertyValue $template "minHostVersion")
Assert-ThreePartVersion -Value $minHostVersion -FieldName "minHostVersion"
if ([Version]$minHostVersion -lt [Version]"0.8.6") {
    throw "PluginSdk 5 releases require minHostVersion 0.8.6 or newer."
}

$releaseNotes = [string](Get-PropertyValue $template "releaseNotes")
$entry = [pscustomobject][ordered]@{
    '$schema' = "https://raw.githubusercontent.com/wwiinnddyy/LanAirApp/main/airappmarket/schema/market-manifest.schema.json"
    schemaVersion = "2.0.0"
    generatedAt = $timestamp
    manifest = [pscustomobject][ordered]@{
        id = [string]$manifest.id
        name = [string]$manifest.name
        description = [string]$manifest.description
        author = [string]$manifest.author
        version = [string]$manifest.version
        apiVersion = [string]$manifest.apiVersion
        entranceAssembly = [string]$manifest.entranceAssembly
        runtime = $manifest.runtime
        sharedContracts = $sharedContracts
        tags = $tags
        releaseTag = $ReleaseTag
        releaseAssetName = $assetName
        releaseNotes = $releaseNotes
    }
    compatibility = [pscustomobject][ordered]@{
        minHostVersion = $minHostVersion
        apiVersion = [string]$manifest.apiVersion
        sharedContracts = $sharedContracts
    }
    repository = [pscustomobject][ordered]@{
        iconUrl = [string](Get-PropertyValue $template "iconUrl")
        projectUrl = $actualRepositoryUrl
        readmeUrl = "https://raw.githubusercontent.com/$($repo.Owner)/$($repo.Name)/main/README.md"
        homepageUrl = [string](Get-PropertyValue $template "homepageUrl")
        repositoryUrl = $actualRepositoryUrl
        tags = $tags
        releaseNotes = $releaseNotes
    }
    publication = [pscustomobject][ordered]@{
        releaseTag = $ReleaseTag
        releaseAssetName = $assetName
        downloadUrl = $releaseDownloadUrl
        sha256 = $hash
        packageSizeBytes = $packageSize
        releaseNotes = $releaseNotes
        packageSources = @(
            [pscustomobject][ordered]@{
                kind = "releaseAsset"
                url = $releaseDownloadUrl
                assetName = $assetName
                sha256 = $hash
                sizeBytes = $packageSize
                releaseTag = $ReleaseTag
                priority = 0
            },
            [pscustomobject][ordered]@{
                kind = "rawFallback"
                url = $rawFallbackUrl
                assetName = $assetName
                sha256 = $hash
                sizeBytes = $packageSize
                releaseTag = $ReleaseTag
                priority = 1
            },
            [pscustomobject][ordered]@{
                kind = "workspaceLocal"
                url = $workspaceLocalUrl
                assetName = $assetName
                sha256 = $hash
                sizeBytes = $packageSize
                releaseTag = $ReleaseTag
                priority = 2
            }
        )
    }
    capabilities = [pscustomobject][ordered]@{
        sharedContracts = $sharedContracts
        desktopComponents = $desktopComponents
        settingsSections = $settingsSections
        exports = $exports
        messageTypes = $messageTypes
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$json = $entry | ConvertTo-Json -Depth 30
Write-Utf8File -Path $OutputPath -Content ($json + [Environment]::NewLine)
Write-Host "Generated market manifest at '$OutputPath'."
