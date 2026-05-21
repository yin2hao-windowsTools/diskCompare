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

internal sealed record NtfsCachedRecord(
    long RecordNumber,
    bool IsDirectory,
    long DataSize,
    long FileNameSize,
    NtfsCachedName[] Names)
{
    public long Size => Math.Max(DataSize, FileNameSize);
}

internal sealed record NtfsCachedName(
    long ParentRecordNumber,
    string Name,
    byte NamespaceId,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc,
    long RealSize)
{
    public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
}
