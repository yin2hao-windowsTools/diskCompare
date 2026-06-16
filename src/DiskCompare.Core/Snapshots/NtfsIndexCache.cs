namespace DiskCompare.Core.Snapshots;

internal sealed record NtfsIndexCache(
    int SchemaVersion,
    string DriveRoot,
    string FileSystem,
    uint VolumeSerialNumber,
    ulong UsnJournalId,
    long NextUsn,
    long LowestValidUsn,
    DateTime UpdatedAtUtc,
    NtfsCachedRecord[] Records)
{
    public const int CurrentSchemaVersion = 1;
}

internal interface INtfsRecord
{
    long RecordNumber { get; }

    bool IsDirectory { get; }

    long Size { get; }

    int NameCount { get; }

    NtfsCachedName GetName(int index);
}

internal sealed record NtfsCachedRecord(
    long RecordNumber,
    bool IsDirectory,
    long DataSize,
    long FileNameSize,
    NtfsCachedName[] Names) : INtfsRecord
{
    public long Size => Math.Max(DataSize, FileNameSize);

    public int NameCount => Names.Length;

    public NtfsCachedName GetName(int index)
    {
        return Names[index];
    }
}

internal sealed record NtfsCachedName(
    long ParentRecordNumber,
    string Name,
    byte NamespaceId,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc,
    long RealSize)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}
