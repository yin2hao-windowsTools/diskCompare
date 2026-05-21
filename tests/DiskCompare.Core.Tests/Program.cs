using DiskCompare.Core.Comparison;
using DiskCompare.Core.Snapshots;

var tests = new (string Name, Action Run)[]
{
    ("Compare aggregates folder growth and shrinkage", CompareAggregatesFolderGrowthAndShrinkage),
    ("Snapshot store round trips compressed data", SnapshotStoreRoundTripsCompressedData)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static void CompareAggregatesFolderGrowthAndShrinkage()
{
    var before = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        new DateTime(2026, 05, 20, 0, 0, 0, DateTimeKind.Utc),
        [
            new FileEntry(Path.Combine("Media", "Video", "a.mp4"), 100, DateTime.UtcNow),
            new FileEntry(Path.Combine("Media", "Audio", "b.wav"), 50, DateTime.UtcNow),
            new FileEntry(Path.Combine("Old", "gone.bin"), 20, DateTime.UtcNow)
        ],
        []);

    var now = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        new DateTime(2026, 05, 21, 0, 0, 0, DateTimeKind.Utc),
        [
            new FileEntry(Path.Combine("Media", "Video", "a.mp4"), 150, DateTime.UtcNow),
            new FileEntry(Path.Combine("Media", "Audio", "b.wav"), 10, DateTime.UtcNow),
            new FileEntry(Path.Combine("New", "fresh.bin"), 30, DateTime.UtcNow)
        ],
        []);

    var comparison = new SnapshotComparer().Compare(before, now);
    AssertEqual(170, comparison.Snapshot.TotalBytes, "Snapshot total");
    AssertEqual(190, comparison.Current.TotalBytes, "Current total");
    AssertEqual(20, comparison.DeltaBytes, "Root delta");

    var media = Find(comparison.Root, "Media");
    AssertEqual(150, media.SnapshotBytes, "Media snapshot bytes");
    AssertEqual(160, media.CurrentBytes, "Media current bytes");
    AssertEqual(10, media.DeltaBytes, "Media delta");

    var old = Find(comparison.Root, "Old");
    AssertEqual(SizeDeltaKind.Removed, old.Kind, "Old kind");

    var added = Find(comparison.Root, "New");
    AssertEqual(SizeDeltaKind.Added, added.Kind, "New kind");
}

static void SnapshotStoreRoundTripsCompressedData()
{
    var tempFile = Path.Combine(Path.GetTempPath(), $"diskcompare-test-{Guid.NewGuid():N}.dcsnap");

    try
    {
        var snapshot = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [new FileEntry(Path.Combine("Folder", "file.txt"), 42, DateTime.UtcNow)],
            [new ScanError(Path.Combine("T:\\", "System Volume Information"), "Access denied")]);

        var store = new SnapshotStore();
        store.SaveAsync(snapshot, tempFile).GetAwaiter().GetResult();
        var loaded = store.LoadAsync(tempFile).GetAwaiter().GetResult();

        AssertEqual(snapshot.DriveRoot, loaded.DriveRoot, "Drive root");
        AssertEqual(snapshot.Files[0].RelativePath, loaded.Files[0].RelativePath, "File path");
        AssertEqual(42, loaded.Files[0].Size, "File size");
        AssertEqual(1, loaded.Errors.Count, "Error count");
    }
    finally
    {
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }
    }
}

static FolderDelta Find(FolderDelta root, string name)
{
    return root.Children.FirstOrDefault(child => child.Name == name)
        ?? throw new InvalidOperationException($"Could not find node {name}.");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}
