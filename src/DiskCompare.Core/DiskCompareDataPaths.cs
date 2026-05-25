namespace DiskCompare.Core;

public static class DiskCompareDataPaths
{
    public static string GetApplicationRootDirectory()
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
    }

    public static string GetSnapshotDirectory()
    {
        return Path.Combine(GetApplicationRootDirectory(), "Snapshots");
    }

    public static string GetIndexCacheDirectory()
    {
        return Path.Combine(GetApplicationRootDirectory(), "IndexCache");
    }

    public static string GetUpdateDirectory()
    {
        return Path.Combine(GetApplicationRootDirectory(), "Updates");
    }
}
