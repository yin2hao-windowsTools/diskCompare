using DiskCompare.Core.Snapshots;

namespace DiskCompare.Core.Comparison;

public sealed record SnapshotComparison(
    Snapshot Snapshot,
    Snapshot Current,
    FolderDelta Root,
    IReadOnlyList<FolderDelta> LargestChanges)
{
    public long DeltaBytes => Current.TotalBytes - Snapshot.TotalBytes;
}
