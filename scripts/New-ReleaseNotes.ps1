param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentTag,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

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

    $groups[$type].Add("$hash $title")
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
$content.Add("What's Changed")

if ($orderedTypes.Count -eq 0) {
    $content.Add("")
    $content.Add("No changes found.")
}
else {
    foreach ($type in $orderedTypes) {
        $content.Add("[$type]:")
        $content.Add("")
        foreach ($entry in $groups[$type]) {
            $content.Add($entry)
        }
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Set-Content -Path $OutputPath -Value $content -Encoding UTF8
