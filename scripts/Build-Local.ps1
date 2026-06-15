[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Runtime = "win-x64",
    [string]$OutputPath = "output",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
$repoWithSeparator = if ($repoFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
    $repoFullPath
}
else {
    $repoFullPath + [System.IO.Path]::DirectorySeparatorChar
}

$isRepoRoot = [string]::Equals($outputRoot, $repoFullPath, [StringComparison]::OrdinalIgnoreCase)
$isInsideRepo = $outputRoot.StartsWith($repoWithSeparator, [StringComparison]::OrdinalIgnoreCase)
if ($isRepoRoot -or -not $isInsideRepo) {
    throw "Output path must be inside the repository and cannot be the repository root: '$outputRoot'."
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

Push-Location $repoRoot
try {
    dotnet restore DiskCompare.slnx

    if (-not $SkipTests) {
        dotnet run --project tests\DiskCompare.Core.Tests\DiskCompare.Core.Tests.csproj --configuration $Configuration --no-restore
    }

    dotnet publish src\DiskCompare.App\DiskCompare.App.csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained false `
        --output $outputRoot `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false

    dotnet publish src\DiskCompare.Launcher\DiskCompare.Launcher.csproj `
        --configuration $Configuration `
        --output $outputRoot `
        -p:DebugType=None `
        -p:DebugSymbols=false

    $launcherExe = Join-Path $outputRoot "DiskCompare.exe"
    $appHostExe = Join-Path $outputRoot "DiskCompare.AppHost.exe"
    if (-not (Test-Path -LiteralPath $launcherExe)) {
        throw "Launcher executable was not found: $launcherExe"
    }

    if (-not (Test-Path -LiteralPath $appHostExe)) {
        throw "Application host executable was not found: $appHostExe"
    }

    Write-Host "Build output copied to: $outputRoot"
}
finally {
    Pop-Location
}
