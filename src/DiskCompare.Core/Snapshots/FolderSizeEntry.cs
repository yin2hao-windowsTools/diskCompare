namespace DiskCompare.Core.Snapshots;

public sealed record FolderSizeEntry(
    string RelativePath,
    string Name,
    long Size);
