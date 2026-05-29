namespace DiskCompare.Launcher;

internal static class RuntimeRequirement
{
    public const int RequiredWindowsDesktopRuntimeMajorVersion = 8;
    public const string DownloadPageUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0";

    public static bool IsSatisfied()
    {
        return HasWindowsDesktopRuntimeMajorVersion(
            GetInstalledWindowsDesktopRuntimeVersions(),
            RequiredWindowsDesktopRuntimeMajorVersion);
    }

    public static string GetMissingRuntimeMessage()
    {
        return
            "未检测到 .NET 8 Desktop Runtime，DiskCompare 无法继续启动。\r\n\r\n" +
            "点击“是”将打开微软 .NET 8 官方下载页，请安装 Windows x64 Desktop Runtime 后再重新启动。";
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
