param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentTag,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

function Get-RepositoryUrl {
    $remoteUrl = (& git remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$remoteUrl")) {
        return "https://github.com/yin2hao-windowsTools/diskCompare"
    }

    $remote = "$remoteUrl".Trim()
    if ($remote -match '^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
        return "https://github.com/$($Matches["owner"])/$($Matches["repo"])"
    }

    if ($remote -match '^git@github\.com:(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$') {
        return "https://github.com/$($Matches["owner"])/$($Matches["repo"])"
    }

    return "https://github.com/yin2hao-windowsTools/diskCompare"
}

function Format-MarkdownText {
    param([string]$Value)

    return $Value.Replace("|", "\|")
}

function Add-ReleaseAssetRow {
    param(
        [System.Collections.Generic.List[string]]$Content,
        [string]$RepositoryUrl,
        [string]$TagName,
        [string]$Platform,
        [string]$Type,
        [string]$FileName
    )

    $downloadUrl = "$RepositoryUrl/releases/download/$TagName/$FileName"
    $Content.Add("| $(Format-MarkdownText $Platform) | $(Format-MarkdownText $Type) | $(Format-MarkdownText $FileName) | [下载]($downloadUrl) |")
}

$currentCommit = (& git rev-list -n 1 $CurrentTag 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$currentCommit")) {
    $currentCommit = (& git rev-parse HEAD).Trim()
}
else {
    $currentCommit = "$currentCommit".Trim()
}

$previousTag = (& git describe --tags --match "v*" --abbrev=0 "$currentCommit^" 2>$null)
if ($LASTEXITCODE -ne 0) {
    $previousTag = ""
}
$previousTag = "$previousTag".Trim()

$range = if ([string]::IsNullOrWhiteSpace($previousTag)) {
    $currentCommit
}
else {
    "$previousTag..$currentCommit"
}

$repositoryUrl = Get-RepositoryUrl
$lines = @(& git log $range --no-merges --pretty=format:"%h%x09%s")
$groups = [ordered]@{}

foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $parts = $line -split "`t", 2
    if ($parts.Count -lt 2) {
        continue
    }

    $hash = $parts[0]
    $subject = $parts[1]
    $type = "other"
    $title = $subject

    if ($subject -match '^\[(?<type>[^\]]+)\]\s*:?\s*(?<title>.+)$') {
        $type = $Matches["type"].Trim().ToLowerInvariant()
        $title = $Matches["title"].Trim()
    }

    if (-not $groups.Contains($type)) {
        $groups[$type] = New-Object System.Collections.Generic.List[string]
    }

    $commitUrl = "$repositoryUrl/commit/$hash"
    $groups[$type].Add("- [$hash]($commitUrl) $(Format-MarkdownText $title)")
}

$preferredOrder = @("fix", "feature", "enhance", "optimize", "document", "docs", "build", "ci", "test", "refactor", "chore", "other")
$orderedTypes = New-Object System.Collections.Generic.List[string]

foreach ($type in $preferredOrder) {
    if ($groups.Contains($type)) {
        $orderedTypes.Add($type)
    }
}

foreach ($type in $groups.Keys) {
    if (-not $orderedTypes.Contains($type)) {
        $orderedTypes.Add($type)
    }
}

$content = New-Object System.Collections.Generic.List[string]
$content.Add("## What's Changed")
$content.Add("")

if ($orderedTypes.Count -eq 0) {
    $content.Add("No changes found.")
}
else {
    foreach ($type in $orderedTypes) {
        $content.Add("[$type]:")
        $content.Add("")
        foreach ($entry in $groups[$type]) {
            $content.Add($entry)
        }
        $content.Add("")
    }
}

if ([string]::IsNullOrWhiteSpace($previousTag)) {
    $content.Add("**Full Changelog:** $repositoryUrl/commits/$CurrentTag")
}
else {
    $content.Add("**Full Changelog:** $repositoryUrl/compare/$previousTag...$CurrentTag")
}

$versionInfo = & (Join-Path $PSScriptRoot "Normalize-Version.ps1") -TagName $CurrentTag | ConvertFrom-Json
$displayVersion = $versionInfo.DisplayVersion
$exeFileName = "DiskCompare-$displayVersion-$Runtime.exe"
$msiFileName = "DiskCompare-$displayVersion-$Runtime.msi"
$portableFileName = "DiskCompare-$displayVersion-$Runtime-portable.zip"

$content.Add("")
$content.Add("## 发行版")
$content.Add("")
$content.Add("| 平台 | 类型 | 文件 | 快速链接 |")
$content.Add("| --- | --- | --- | --- |")
Add-ReleaseAssetRow -Content $content -RepositoryUrl $repositoryUrl -TagName $CurrentTag -Platform "Windows" -Type "EXE installer" -FileName $exeFileName
Add-ReleaseAssetRow -Content $content -RepositoryUrl $repositoryUrl -TagName $CurrentTag -Platform "Windows" -Type "MSI installer" -FileName $msiFileName
Add-ReleaseAssetRow -Content $content -RepositoryUrl $repositoryUrl -TagName $CurrentTag -Platform "Windows" -Type "portable ZIP" -FileName $portableFileName

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Set-Content -Path $OutputPath -Value $content -Encoding UTF8
