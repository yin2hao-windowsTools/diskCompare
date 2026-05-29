using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using DiskCompare.Core;

namespace DiskCompare.App;

public sealed class ApplicationUpdater
{
    private static readonly HttpClient HttpClient = new();

    public ReleaseAsset? SelectPreferredAsset(ReleaseInfo release)
    {
        var portableArchive = release.Assets.FirstOrDefault(IsPortableArchiveAsset);
        var executable = release.Assets.FirstOrDefault(IsPortableExecutableAsset);
        var installer = release.Assets.FirstOrDefault(IsMsiInstallerAsset);

        if (IsCurrentExecutableUnderProgramFiles() && installer is not null)
        {
            return installer;
        }

        return portableArchive ?? executable ?? installer;
    }

    public static UpdatePackageKind? GetPackageKind(ReleaseAsset asset)
    {
        var extension = Path.GetExtension(asset.Name);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageKind.PortableExecutable;
        }

        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageKind.MsiInstaller;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return UpdatePackageKind.PortableArchive;
        }

        return null;
    }

    public async Task<DownloadedUpdatePackage> DownloadAsync(
        ReleaseAsset asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var kind = GetPackageKind(asset)
            ?? throw new InvalidOperationException($"不支持的更新文件类型: {asset.Name}");
        var updateDirectory = Path.Combine(DiskCompareDataPaths.GetUpdateDirectory(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);

        var packagePath = Path.Combine(updateDirectory, GetSafeFileName(asset.Name));
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd($"DiskCompare/{ReleaseUpdateService.GetCurrentDisplayVersion()}");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 128];
        long receivedBytes = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            receivedBytes += read;
            progress?.Report(new UpdateDownloadProgress(receivedBytes, totalBytes));
        }

        return new DownloadedUpdatePackage(kind, packagePath, asset);
    }

    public void ApplyUpdateAndRestart(DownloadedUpdatePackage package)
    {
        if (package.Kind == UpdatePackageKind.MsiInstaller)
        {
            StartMsiInstaller(package.FilePath);
            return;
        }

        if (package.Kind == UpdatePackageKind.PortableArchive)
        {
            StartPortableArchiveReplacement(package.FilePath);
            return;
        }

        StartPortableExecutableReplacement(package.FilePath);
    }

    private static bool IsPortableArchiveAsset(ReleaseAsset asset)
    {
        return GetPackageKind(asset) == UpdatePackageKind.PortableArchive
            && asset.Name.Contains("DiskCompare", StringComparison.OrdinalIgnoreCase)
            && asset.Name.Contains("portable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableExecutableAsset(ReleaseAsset asset)
    {
        return GetPackageKind(asset) == UpdatePackageKind.PortableExecutable
            && asset.Name.Contains("DiskCompare", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMsiInstallerAsset(ReleaseAsset asset)
    {
        return GetPackageKind(asset) == UpdatePackageKind.MsiInstaller
            && asset.Name.Contains("DiskCompare", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentExecutableUnderProgramFiles()
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return IsPathInsideDirectory(currentPath, programFiles) || IsPathInsideDirectory(currentPath, programFilesX86);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory);
        var directoryWithSeparator = Path.EndsInDirectorySeparator(fullDirectory)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSafeFileName(string name)
    {
        var fileName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "DiskCompare-update";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidCharacter, '_');
        }

        return fileName;
    }

    private static void StartMsiInstaller(string packagePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i {QuoteCommandLineArgument(packagePath)}",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static void StartPortableExecutableReplacement(string packagePath)
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            throw new InvalidOperationException("无法确定当前程序路径，不能自动覆盖安装。");
        }

        if (!Path.GetExtension(currentPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前运行方式不是可覆盖的 exe，请从 Release 页面手动下载更新。");
        }

        var updateDirectory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("无法确定更新文件目录。");
        StartPortableReplacement(packagePath, currentPath, updateDirectory, GetPortableExecutableReplacementScript());
    }

    private static void StartPortableArchiveReplacement(string packagePath)
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            throw new InvalidOperationException("无法确定当前程序路径，不能自动覆盖安装。");
        }

        var updateDirectory = Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("无法确定更新文件目录。");
        StartPortableReplacement(packagePath, currentPath, updateDirectory, GetPortableArchiveReplacementScript());
    }

    private static void StartPortableReplacement(string packagePath, string currentPath, string updateDirectory, string script)
    {
        var scriptPath = Path.Combine(updateDirectory, "Apply-DiskCompareUpdate.ps1");
        var logPath = Path.Combine(updateDirectory, "update-error.log");
        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-SourcePath");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("-TargetPath");
        startInfo.ArgumentList.Add(currentPath);
        startInfo.ArgumentList.Add("-RestartPath");
        startInfo.ArgumentList.Add(currentPath);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);

        Process.Start(startInfo);
    }

    private static string GetPortableExecutableReplacementScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$RestartPath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

try {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
    }

    Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Force
    Unblock-File -LiteralPath $TargetPath -ErrorAction SilentlyContinue
    Start-Process -FilePath $RestartPath
}
catch {
    $_ | Out-String | Set-Content -LiteralPath $LogPath -Encoding UTF8
    Start-Process -FilePath $SourcePath
}
""";
    }

    private static string GetPortableArchiveReplacementScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$RestartPath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

try {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
    }

    $targetDirectory = Split-Path -Parent $TargetPath
    $extractDirectory = Join-Path (Split-Path -Parent $SourcePath) 'expanded'
    if (Test-Path -LiteralPath $extractDirectory) {
        Remove-Item -LiteralPath $extractDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
    Expand-Archive -LiteralPath $SourcePath -DestinationPath $extractDirectory -Force

    $sourceRoot = $extractDirectory
    $children = @(Get-ChildItem -LiteralPath $extractDirectory -Force)
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $sourceRoot = $children[0].FullName
    }

    $updatedExecutable = Join-Path $sourceRoot (Split-Path -Leaf $TargetPath)
    if (-not (Test-Path -LiteralPath $updatedExecutable -PathType Leaf)) {
        throw "Portable archive does not contain $(Split-Path -Leaf $TargetPath)."
    }

    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $targetDirectory -Recurse -Force
    }

    Unblock-File -LiteralPath $RestartPath -ErrorAction SilentlyContinue
    Start-Process -FilePath $RestartPath
}
catch {
    $_ | Out-String | Set-Content -LiteralPath $LogPath -Encoding UTF8
    Start-Process -FilePath $SourcePath
}
""";
    }

    private static string QuoteCommandLineArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}

public enum UpdatePackageKind
{
    PortableExecutable,
    MsiInstaller,
    PortableArchive
}

public sealed record UpdateDownloadProgress(long ReceivedBytes, long TotalBytes);

public sealed record DownloadedUpdatePackage(UpdatePackageKind Kind, string FilePath, ReleaseAsset Asset);
