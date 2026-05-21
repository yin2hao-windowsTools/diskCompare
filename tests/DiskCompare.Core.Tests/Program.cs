using DiskCompare.Core.Comparison;
using DiskCompare.Core.Snapshots;

var tests = new (string Name, Action Run)[]
{
    ("Compare aggregates folder growth and shrinkage", CompareAggregatesFolderGrowthAndShrinkage),
    ("Snapshot store round trips compressed data", SnapshotStoreRoundTripsCompressedData),
    ("Snapshot store rejects unsafe output paths", SnapshotStoreRejectsUnsafeOutputPaths),
    ("Snapshot store rejects unsafe input paths", SnapshotStoreRejectsUnsafeInputPaths)
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
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var snapshot = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [new FileEntry(Path.Combine("Folder", "file.txt"), 42, DateTime.UtcNow)],
            [new ScanError(Path.Combine("T:\\", "System Volume Information"), "Access denied")]);

        var store = new SnapshotStore(tempRoot);
        var tempFile = store.CreateDefaultSnapshotPath(snapshot);
        store.SaveAsync(snapshot, tempFile).GetAwaiter().GetResult();
        var loaded = store.LoadAsync(tempFile).GetAwaiter().GetResult();

        AssertEqual(snapshot.DriveRoot, loaded.DriveRoot, "Drive root");
        AssertEqual(snapshot.Files[0].RelativePath, loaded.Files[0].RelativePath, "File path");
        AssertEqual(42, loaded.Files[0].Size, "File size");
        AssertEqual(1, loaded.Errors.Count, "Error count");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void SnapshotStoreRejectsUnsafeOutputPaths()
{
    var tempRoot = CreateOwnedTempDirectory();
    var outsideRoot = CreateOwnedTempDirectory();

    try
    {
        var snapshot = new Snapshot("T:\\", "Test", "NTFS", DateTime.UtcNow, [], []);
        var store = new SnapshotStore(tempRoot);
        var safePath = store.CreateDefaultSnapshotPath(snapshot);
        store.SaveAsync(snapshot, safePath).GetAwaiter().GetResult();

        AssertThrows<IOException>(
            () => store.SaveAsync(snapshot, safePath).GetAwaiter().GetResult(),
            "Existing snapshot overwrite");

        AssertThrows<InvalidOperationException>(
            () => store.SaveAsync(snapshot, Path.Combine(outsideRoot, "outside.dcsnap")).GetAwaiter().GetResult(),
            "Outside output path");

        AssertThrows<InvalidOperationException>(
            () => store.SaveAsync(snapshot, Path.Combine(tempRoot, "wrong.txt")).GetAwaiter().GetResult(),
            "Wrong extension");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
        DeleteOwnedTempDirectory(outsideRoot);
    }
}

static void SnapshotStoreRejectsUnsafeInputPaths()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var wrongExtension = Path.Combine(tempRoot, "not-a-snapshot.txt");
        File.WriteAllText(wrongExtension, "not a snapshot");

        AssertThrows<InvalidOperationException>(
            () => new SnapshotStore(tempRoot).LoadAsync(wrongExtension).GetAwaiter().GetResult(),
            "Wrong input extension");

        AssertThrows<FileNotFoundException>(
            () => new SnapshotStore(tempRoot).LoadAsync(Path.Combine(tempRoot, "missing.dcsnap")).GetAwaiter().GetResult(),
            "Missing input file");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
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

static void AssertThrows<TException>(Action action, string label)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{label}: expected exception {typeof(TException).Name}.");
}

static string CreateOwnedTempDirectory()
{
    var root = Path.Combine(Path.GetTempPath(), "DiskCompare.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    return root;
}

static void DeleteOwnedTempDirectory(string path)
{
    var fullPath = Path.GetFullPath(path);
    var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DiskCompare.Tests"));
    if (!IsPathInsideDirectory(fullPath, allowedRoot))
    {
        throw new InvalidOperationException($"Refusing to delete path outside owned test temp directory: {fullPath}");
    }

    if (Directory.Exists(fullPath))
    {
        foreach (var file in Directory.EnumerateFiles(fullPath))
        {
            File.Delete(file);
        }

        if (Directory.EnumerateDirectories(fullPath).Any())
        {
            throw new InvalidOperationException($"Refusing recursive delete in test cleanup: {fullPath}");
        }

        Directory.Delete(fullPath, recursive: false);
    }
}

static bool IsPathInsideDirectory(string path, string directory)
{
    var normalizedDirectory = Path.EndsInDirectorySeparator(directory)
        ? directory
        : directory + Path.DirectorySeparatorChar;

    return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
}
