param(
    [Parameter(Mandatory = $true)]
    [string]$TagName
)

$tag = $TagName.Trim()
if ([string]::IsNullOrWhiteSpace($tag)) {
    throw "Tag name is required."
}

$rawVersion = if ($tag.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
    $tag.Substring(1)
}
else {
    $tag
}

$segments = @($rawVersion -split '\.')
if ($segments.Count -eq 0) {
    throw "Tag '$TagName' does not contain a version number."
}

$numbers = New-Object System.Collections.Generic.List[int]
foreach ($segment in $segments) {
    if ($numbers.Count -ge 3) {
        break
    }

    if ($segment -notmatch '^\d+$') {
        throw "Version segment '$segment' in tag '$TagName' is not a non-negative integer."
    }

    $value = [int]$segment
    if ($value -gt 65535) {
        throw "Version segment '$segment' in tag '$TagName' is larger than 65535."
    }

    $numbers.Add($value)
}

while ($numbers.Count -lt 3) {
    $numbers.Add(0)
}

$version = "{0}.{1}.{2}" -f $numbers[0], $numbers[1], $numbers[2]
[pscustomobject]@{
    Version = $version
    FileVersion = "$version.0"
    DisplayVersion = "v$version"
} | ConvertTo-Json -Compress
