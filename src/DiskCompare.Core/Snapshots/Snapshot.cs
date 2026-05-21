namespace DiskCompare.Core.Snapshots;

public sealed record Snapshot(
    string DriveRoot,
    string VolumeLabel,
    string FileSystem,
    DateTime CreatedAtUtc,
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<ScanError> Errors)
{
    public long TotalBytes => Files.Sum(static file => file.Size);

    public int FileCount => Files.Count;
}
