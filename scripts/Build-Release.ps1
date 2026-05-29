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

function ConvertTo-WixIdentifier {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $hashBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Value))
    $hash = [System.BitConverter]::ToString($hashBytes).Replace("-", "").Substring(0, 12).ToLowerInvariant()
    $name = [System.Text.RegularExpressions.Regex]::Replace($Value, "[^A-Za-z0-9_\.]", "_")
    if ([string]::IsNullOrWhiteSpace($name) -or -not [char]::IsLetter($name[0])) {
        $name = "item_$name"
    }

    if ($name.Length -gt 40) {
        $name = $name.Substring(0, 40)
    }

    return "${Prefix}_${name}_$hash"
}

function ConvertTo-XmlAttributeValue {
    param([Parameter(Mandatory = $true)][string]$Value)

    return [System.Security.SecurityElement]::Escape($Value)
}

function New-WixPublishedFilesInclude {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDir,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $directoryIds = @{}
    $directoryIds[""] = "INSTALLFOLDER"
    $lines = New-Object System.Collections.Generic.List[string]
    $componentRefs = New-Object System.Collections.Generic.List[string]

    $lines.Add("<Include xmlns=`"http://wixtoolset.org/schemas/v4/wxs`">")

    $directories = Get-ChildItem -LiteralPath $PublishDir -Directory -Recurse |
        Sort-Object { [System.IO.Path]::GetRelativePath($PublishDir, $_.FullName).Split([System.IO.Path]::DirectorySeparatorChar).Count }

    foreach ($directory in $directories) {
        $relativePath = [System.IO.Path]::GetRelativePath($PublishDir, $directory.FullName)
        $parentRelativePath = [System.IO.Path]::GetRelativePath($PublishDir, (Split-Path -Parent $directory.FullName))
        if ($parentRelativePath -eq ".") {
            $parentRelativePath = ""
        }

        $directoryId = ConvertTo-WixIdentifier -Prefix "Dir" -Value $relativePath
        $directoryIds[$relativePath] = $directoryId
        $parentDirectoryId = $directoryIds[$parentRelativePath]
        $directoryName = ConvertTo-XmlAttributeValue (Split-Path -Leaf $directory.FullName)

        $lines.Add("  <Fragment>")
        $lines.Add("    <DirectoryRef Id=`"$parentDirectoryId`">")
        $lines.Add("      <Directory Id=`"$directoryId`" Name=`"$directoryName`" />")
        $lines.Add("    </DirectoryRef>")
        $lines.Add("  </Fragment>")
    }

    $files = Get-ChildItem -LiteralPath $PublishDir -File -Recurse |
        Sort-Object { [System.IO.Path]::GetRelativePath($PublishDir, $_.FullName) }

    foreach ($file in $files) {
        $relativePath = [System.IO.Path]::GetRelativePath($PublishDir, $file.FullName)
        $relativeDirectory = Split-Path -Parent $relativePath
        if ($relativeDirectory -eq ".") {
            $relativeDirectory = ""
        }

        $directoryId = $directoryIds[$relativeDirectory]
        $componentId = ConvertTo-WixIdentifier -Prefix "Cmp" -Value $relativePath
        $fileId = ConvertTo-WixIdentifier -Prefix "File" -Value $relativePath
        $sourcePath = ConvertTo-XmlAttributeValue $file.FullName

        $lines.Add("  <Fragment>")
        $lines.Add("    <DirectoryRef Id=`"$directoryId`">")
        $lines.Add("      <Component Id=`"$componentId`" Guid=`"*`">")
        $lines.Add("        <File Id=`"$fileId`" Source=`"$sourcePath`" KeyPath=`"yes`" />")
        $lines.Add("      </Component>")
        $lines.Add("    </DirectoryRef>")
        $lines.Add("  </Fragment>")
        $componentRefs.Add("      <ComponentRef Id=`"$componentId`" />")
    }

    $lines.Add("  <Fragment>")
    $lines.Add("    <ComponentGroup Id=`"PublishedFiles`">")
    foreach ($componentRef in $componentRefs) {
        $lines.Add($componentRef)
    }
    $lines.Add("    </ComponentGroup>")
    $lines.Add("  </Fragment>")
    $lines.Add("</Include>")

    Set-Content -Path $OutputPath -Value $lines -Encoding UTF8
}

function Add-WixExtension {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    & $WixPath extension add "$Extension/4.0.6" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add WiX extension $Extension."
    }
}

function Assert-LastExitCode {
    param([Parameter(Mandatory = $true)][string]$CommandName)

    if ($LASTEXITCODE -ne 0) {
        throw "$CommandName failed with exit code $LASTEXITCODE."
    }
}

$artifactsRoot = Resolve-InRepoPath $OutputRoot
$portablePublishDir = Join-Path $artifactsRoot "publish\$Runtime-portable"
$releaseDir = Join-Path $artifactsRoot "release"
$releaseNotesPath = Join-Path $artifactsRoot "release-notes.md"
$generatedFilesWxi = Join-Path $artifactsRoot "installer\PublishedFiles.wxi"

if (Test-Path $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $portablePublishDir, $releaseDir, (Split-Path -Parent $generatedFilesWxi) | Out-Null

Push-Location $repoRoot
try {
    dotnet restore DiskCompare.slnx
    dotnet run --project tests\DiskCompare.Core.Tests\DiskCompare.Core.Tests.csproj --configuration $Configuration --no-restore

    dotnet publish src\DiskCompare.App\DiskCompare.App.csproj `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $portablePublishDir `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$version `
        -p:AssemblyVersion=$fileVersion `
        -p:FileVersion=$fileVersion `
        -p:InformationalVersion=$displayVersion

    $portableExeSource = Join-Path $portablePublishDir "DiskCompare.exe"
    if (-not (Test-Path $portableExeSource)) {
        throw "Portable executable was not found: $portableExeSource"
    }

    $exeArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime.exe"
    $portableArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime-portable.zip"
    $msiArtifact = Join-Path $releaseDir "DiskCompare-$displayVersion-$Runtime.msi"

    Compress-Archive -Path (Join-Path $portablePublishDir "*") -DestinationPath $portableArtifact -Force
    New-WixPublishedFilesInclude -PublishDir $portablePublishDir -OutputPath $generatedFilesWxi
    Add-WixExtension -Extension "WixToolset.UI.wixext"
    Add-WixExtension -Extension "WixToolset.Bal.wixext"

    & $WixPath build installer\DiskCompare.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -d "ProductVersion=$version" `
        -d "GeneratedFilesWxi=$generatedFilesWxi" `
        -d "IconPath=$(Join-Path $repoRoot "src\DiskCompare.App\Assets\DiskCompare.ico")" `
        -pdbtype none `
        -out $msiArtifact
    Assert-LastExitCode "WiX MSI build"

    & $WixPath build installer\DiskCompare.Bundle.wxs `
        -arch x64 `
        -ext WixToolset.Bal.wixext `
        -d "ProductVersion=$version" `
        -d "MsiPath=$msiArtifact" `
        -d "IconPath=$(Join-Path $repoRoot "src\DiskCompare.App\Assets\DiskCompare.ico")" `
        -out $exeArtifact
    Assert-LastExitCode "WiX EXE installer build"

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
