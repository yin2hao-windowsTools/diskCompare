namespace DiskCompare.Launcher;

internal static class RuntimeRequirement
{
    public const int RequiredWindowsDesktopRuntimeMajorVersion = 8;
    public const string DownloadPageUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0";
    public const string DownloadButtonText = "打开 .NET 8 官网";
    public const string DownloadLinkCaption = "Microsoft .NET 8 Desktop Runtime 官方下载页";

    public static bool IsSatisfied()
    {
        return HasWindowsDesktopRuntimeMajorVersion(
            GetInstalledWindowsDesktopRuntimeVersions(),
            RequiredWindowsDesktopRuntimeMajorVersion);
    }

    public static string GetMissingRuntimeMessage()
    {
        return
            "未检测到 .NET 8 Desktop Runtime，DiskCompare 暂时无法启动。\r\n\r\n" +
            "请打开微软官网，安装 Windows x64 Desktop Runtime 后重新启动 DiskCompare。";
    }

    public static string GetMissingRuntimeFootnote()
    {
        return "下载安装页里请选择 “.NET Desktop Runtime 8” 下的 Windows x64 安装包。";
    }

    public static void OpenDownloadPage()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = DownloadPageUrl,
            UseShellExecute = true
        });
    }

    internal static IReadOnlyList<string> GetInstalledWindowsDesktopRuntimeVersions()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return [];
        }

        var runtimeRoot = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(runtimeRoot))
        {
            return [];
        }

        return Directory.GetDirectories(runtimeRoot)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
    }

    internal static bool HasWindowsDesktopRuntimeMajorVersion(IEnumerable<string> versionFolderNames, int requiredMajorVersion)
    {
        foreach (var versionFolderName in versionFolderNames)
        {
            if (Version.TryParse(versionFolderName, out var version) && version.Major == requiredMajorVersion)
            {
                return true;
            }
        }

        return false;
    }
}
