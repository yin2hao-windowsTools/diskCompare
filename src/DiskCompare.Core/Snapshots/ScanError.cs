namespace DiskCompare.Core.Snapshots;

public sealed record ScanError(
    string Path,
    string Message);
