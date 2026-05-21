namespace DiskCompare.Core.Snapshots;

public sealed record FileEntry(
    string RelativePath,
    long Size,
    DateTime LastWriteTimeUtc);
