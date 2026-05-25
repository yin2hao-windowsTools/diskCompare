param(
    [Parameter(Mandatory = $true)]
    [string]$TagName,

    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts",
    [string]$WixPath = "wix"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionInfo = & (Join-Path $PSScriptRoot "Normalize-Version.ps1") -TagName $TagName | ConvertFrom-Json
$version = $versionInfo.Version
$fileVersion = $versionInfo.FileVersion
$displayVersion = $versionInfo.DisplayVersion

function Resolve-InRepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
    $repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
    $repoWithSeparator = if ([System.IO.Path]::EndsInDirectorySeparator($repoFullPath)) {
        $repoFullPath
    }
    else {
        $repoFullPath + [System.IO.Path]::DirectorySeparatorChar
    }

    $isRepoRoot = [string]::Equals($fullPath, $repoFullPath, [StringComparison]::OrdinalIgnoreCase)
    $isInsideRepo = $fullPath.StartsWith($repoWithSeparator, [StringComparison]::OrdinalIgnoreCase)
    if (-not ($isRepoRoot -or $isInsideRepo)) {
        throw "Path '$fullPath' is outside repository root '$repoFullPath'."
    }

    $expectedArtifactsPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
    if (-not [string]::Equals($fullPath, $expectedArtifactsPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output root must be the repository artifacts directory: '$expectedArtifactsPath'."
    }

    return $fullPath
}

$artifactsRoot = Resolve-InRepoPath $OutputRoot
$publishDir = Join-Path $artifactsRoot "publish\$Runtime"
$releaseDir = Join-Path $artifactsRoot "release"
$releaseNotesPath = Join-Path $artifactsRoot "release-notes.md"

if (Test-Path $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $releaseDir | Out-Null

Push-Location $repoRoot
try {
    dotnet restore DiskCompare.slnx
    dotnet run --project tests\DiskCompare.Core.Tests\DiskCompare.Core.Tests.csproj --configuration $Configuration --no-restore

    dotnet publish src\DiskCompare.App\DiskCompare.App.csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $publishDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$version `
        -p:AssemblyVersion=$fileVersion `
        -p:FileVersion=$fileVersion `
        -p:InformationalVersion=$displayVersion

    $exeSource = Join-Path $publishDir "DiskCompare.exe"
    if (-not (Test-Path $exeSource)) {
        throw "Published executable was not found: $exeSource"
    }

    $exeArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime.exe"
    $portableArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime-portable.zip"
    $msiArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime.msi"

    Copy-Item -LiteralPath $exeSource -Destination $exeArtifact
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableArtifact -Force

    & $WixPath build installer\DiskCompare.wxs `
        -arch x64 `
        -d "ProductVersion=$version" `
        -d "PublishDir=$publishDir" `
        -pdbtype none `
        -out $msiArtifact

    & (Join-Path $PSScriptRoot "New-ReleaseNotes.ps1") -CurrentTag $TagName -OutputPath $releaseNotesPath -Runtime $Runtime

    [pscustomobject]@{
        Version = $version
        FileVersion = $fileVersion
        DisplayVersion = $displayVersion
        ReleaseDirectory = [System.IO.Path]::GetRelativePath($repoRoot, $releaseDir)
        ReleaseNotes = [System.IO.Path]::GetRelativePath($repoRoot, $releaseNotesPath)
        Exe = [System.IO.Path]::GetRelativePath($repoRoot, $exeArtifact)
        Portable = [System.IO.Path]::GetRelativePath($repoRoot, $portableArtifact)
        Msi = [System.IO.Path]::GetRelativePath($repoRoot, $msiArtifact)
    } | ConvertTo-Json -Compress
}
finally {
    Pop-Location
}
