namespace DiskCompare.Core.Comparison;

public sealed class FolderDelta
{
    public FolderDelta(string name, string relativePath)
    {
        Name = name;
        RelativePath = relativePath;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public long SnapshotBytes { get; internal set; }

    public long CurrentBytes { get; internal set; }

    public long DeltaBytes => CurrentBytes - SnapshotBytes;

    public double ChangeRatio
    {
        get
        {
            if (SnapshotBytes == 0)
            {
                return CurrentBytes == 0 ? 0 : 1;
            }

            return (double)DeltaBytes / SnapshotBytes;
        }
    }

    public SizeDeltaKind Kind
    {
        get
        {
            if (SnapshotBytes == 0 && CurrentBytes > 0)
            {
                return SizeDeltaKind.Added;
            }

            if (SnapshotBytes > 0 && CurrentBytes == 0)
            {
                return SizeDeltaKind.Removed;
            }

            if (DeltaBytes > 0)
            {
                return SizeDeltaKind.Increased;
            }

            if (DeltaBytes < 0)
            {
                return SizeDeltaKind.Decreased;
            }

            return SizeDeltaKind.Unchanged;
        }
    }

    public List<FolderDelta> Children { get; } = [];
}
