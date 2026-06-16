using System.Text.Json.Serialization;

namespace DiskCompare.Core.Snapshots;

public sealed record Snapshot(
    string DriveRoot,
    string VolumeLabel,
    string FileSystem,
    DateTime CreatedAtUtc,
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<ScanError> Errors,
    IReadOnlyList<FolderSizeEntry>? Folders = null,
    long? TotalBytesOverride = null,
    int? FileCountOverride = null)
{
    [JsonIgnore]
    public long TotalBytes => TotalBytesOverride ?? (Files.Count > 0
        ? Files.Sum(static file => file.Size)
        : FolderSizes.Where(static folder => !HasParent(folder.RelativePath)).Sum(static folder => folder.Size));

    [JsonIgnore]
    public int FileCount => FileCountOverride ?? Files.Count;

    [JsonIgnore]
    public IReadOnlyList<FolderSizeEntry> FolderSizes => Folders ?? [];

    private static bool HasParent(string relativePath)
    {
        return relativePath.Contains(Path.DirectorySeparatorChar) || relativePath.Contains(Path.AltDirectorySeparatorChar);
    }
}
