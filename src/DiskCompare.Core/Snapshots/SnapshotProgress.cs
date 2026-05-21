namespace DiskCompare.Core.Snapshots;

public sealed record SnapshotProgress(
    string CurrentPath,
    int FilesScanned,
    long BytesScanned,
    int ErrorCount);
