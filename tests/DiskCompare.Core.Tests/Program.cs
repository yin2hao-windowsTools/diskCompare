using DiskCompare.App;
using DiskCompare.Core;
using DiskCompare.Core.Comparison;
using DiskCompare.Core.Snapshots;
using DiskCompare.Launcher;

var tests = new (string Name, Action Run)[]
{
    ("Compare aggregates folder growth and shrinkage", CompareAggregatesFolderGrowthAndShrinkage),
    ("Snapshot progress exposes scan mode", SnapshotProgressExposesScanMode),
    ("Snapshot store round trips compressed data", SnapshotStoreRoundTripsCompressedData),
    ("Snapshot store rejects unsafe output paths", SnapshotStoreRejectsUnsafeOutputPaths),
    ("Snapshot store rejects unsafe input paths", SnapshotStoreRejectsUnsafeInputPaths),
    ("Snapshot store rejects invalid loaded entries", SnapshotStoreRejectsInvalidLoadedEntries),
    ("Snapshot store rejects invalid aggregate file count", SnapshotStoreRejectsInvalidAggregateFileCount),
    ("Snapshot store rejects oversized compressed files", SnapshotStoreRejectsOversizedCompressedFiles),
    ("Snapshot supports aggregate file count override", SnapshotSupportsAggregateFileCountOverride),
    ("Default data paths stay under application root", DefaultDataPathsStayUnderApplicationRoot),
    ("Snapshot comparer handles mixed separators without parent split", SnapshotComparerHandlesMixedSeparators),
    ("Snapshot comparer uses folder aggregates when present", SnapshotComparerUsesFolderAggregatesWhenPresent),
    ("Snapshot comparer uses aggregate root total without folders", SnapshotComparerUsesAggregateRootTotalWithoutFolders),
    ("Snapshot comparer mixes legacy files and folder aggregates", SnapshotComparerMixesLegacyFilesAndFolderAggregates),
    ("Directory snapshot stores aggregate folder sizes", DirectorySnapshotStoresAggregateFolderSizes),
    ("Application updater prefers portable archive outside Program Files", ApplicationUpdaterPrefersPortableArchiveOutsideProgramFiles),
    ("Application updater recognizes executable installer package", ApplicationUpdaterRecognizesExecutableInstallerPackage),
    ("Application updater recognizes portable archive package", ApplicationUpdaterRecognizesPortableArchivePackage),
    (".NET launcher detects required desktop runtime major version", RuntimeRequirementDetectsRequiredDesktopRuntimeMajorVersion),
    (".NET launcher rejects missing desktop runtime major version", RuntimeRequirementRejectsMissingDesktopRuntimeMajorVersion),
    ("NTFS index cache stores unique files and loads newest USN", NtfsIndexCacheStoresUniqueFilesAndLoadsNewestUsn),
    ("NTFS index cache ignores malicious counts", NtfsIndexCacheIgnoresMaliciousCounts),
    ("NTFS index cache factory preserves record data", NtfsIndexCacheFactoryPreservesRecordData),
    ("NTFS MFT aggregate snapshot rolls folder sizes upward", NtfsMftAggregateSnapshotRollsFolderSizesUpward),
    ("NTFS MFT aggregate snapshot ignores orphan files", NtfsMftAggregateSnapshotIgnoresOrphanFiles),
    ("NTFS MFT aggregate snapshot keeps preferred names and skips reparse points", NtfsMftAggregateSnapshotKeepsPreferredNamesAndSkipsReparsePoints),
    ("NTFS MFT aggregate snapshot accepts unnamed file records", NtfsMftAggregateSnapshotAcceptsUnnamedFileRecords)
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

static void SnapshotProgressExposesScanMode()
{
    var progress = new SnapshotProgress("C:\\", 10, 1024, 1, "NTFS MFT 快速索引");
    AssertEqual("NTFS MFT 快速索引", progress.Mode, "Progress mode");
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

static void SnapshotStoreRejectsInvalidLoadedEntries()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var invalid = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [new FileEntry("C:\\absolute.bin", 1, DateTime.UtcNow)],
            []);
        var store = new SnapshotStore(tempRoot);
        var path = store.CreateDefaultSnapshotPath(invalid);
        store.SaveAsync(invalid, path).GetAwaiter().GetResult();

        AssertThrows<InvalidDataException>(
            () => store.LoadAsync(path).GetAwaiter().GetResult(),
            "Invalid absolute file path");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void SnapshotStoreRejectsInvalidAggregateFileCount()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var invalid = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [],
            [],
            [new FolderSizeEntry("Media", "Media", 1)],
            1,
            -1);
        var store = new SnapshotStore(tempRoot);
        var path = store.CreateDefaultSnapshotPath(invalid);
        store.SaveAsync(invalid, path).GetAwaiter().GetResult();

        AssertThrows<InvalidDataException>(
            () => store.LoadAsync(path).GetAwaiter().GetResult(),
            "Invalid aggregate file count");

        var invalidTotal = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [],
            [],
            [new FolderSizeEntry("Media", "Media", 1)],
            -1,
            1);
        var totalPath = store.CreateDefaultSnapshotPath(invalidTotal);
        store.SaveAsync(invalidTotal, totalPath).GetAwaiter().GetResult();

        AssertThrows<InvalidDataException>(
            () => store.LoadAsync(totalPath).GetAwaiter().GetResult(),
            "Invalid aggregate total size");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void SnapshotStoreRejectsOversizedCompressedFiles()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var path = Path.Combine(tempRoot, "oversized.dcsnap");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.SetLength((256L * 1024 * 1024) + 1);
        }

        AssertThrows<InvalidDataException>(
            () => new SnapshotStore(tempRoot).LoadAsync(path).GetAwaiter().GetResult(),
            "Oversized compressed snapshot");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void SnapshotSupportsAggregateFileCountOverride()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var snapshot = new Snapshot(
            "T:\\",
            "Test",
            "NTFS",
            DateTime.UtcNow,
            [],
            [],
            [new FolderSizeEntry("Media", "Media", 42)],
            42,
            12);

        var store = new SnapshotStore(tempRoot);
        var path = store.CreateDefaultSnapshotPath(snapshot);
        store.SaveAsync(snapshot, path).GetAwaiter().GetResult();
        var loaded = store.LoadAsync(path).GetAwaiter().GetResult();

        AssertEqual(12, loaded.FileCount, "Aggregate snapshot file count");
        AssertEqual(42L, loaded.TotalBytes, "Aggregate snapshot total bytes");
    }
    finally
    {
        DeleteOwnedTempDirectoryContents(tempRoot);
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void DefaultDataPathsStayUnderApplicationRoot()
{
    var applicationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    AssertEqual(applicationRoot, DiskCompareDataPaths.GetApplicationRootDirectory(), "Application data root");
    AssertEqual(Path.Combine(applicationRoot, "Snapshots"), new SnapshotStore().GetDefaultSnapshotDirectory(), "Default snapshot directory");
    AssertEqual(Path.Combine(applicationRoot, "IndexCache"), DiskCompareDataPaths.GetIndexCacheDirectory(), "Default index cache directory");
    AssertEqual(Path.Combine(applicationRoot, "Updates"), DiskCompareDataPaths.GetUpdateDirectory(), "Default update directory");
}

static void SnapshotComparerHandlesMixedSeparators()
{
    var before = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [
            new FileEntry("root.bin", 5, DateTime.UtcNow),
            new FileEntry("A/B\\c.bin", 15, DateTime.UtcNow)
        ],
        []);
    var now = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [new FileEntry("A/B\\c.bin", 25, DateTime.UtcNow)],
        []);

    var comparison = new SnapshotComparer().Compare(before, now);
    AssertEqual(5, comparison.DeltaBytes, "Root mixed separator delta");
    var a = Find(comparison.Root, "A");
    AssertEqual(10, a.DeltaBytes, "A delta");
    var b = Find(a, "B");
    AssertEqual(10, b.DeltaBytes, "B delta");
}

static void SnapshotComparerUsesFolderAggregatesWhenPresent()
{
    var before = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [],
        [],
        [
            new FolderSizeEntry("Media", "Media", 150),
            new FolderSizeEntry(Path.Combine("Media", "Video"), "Video", 100),
            new FolderSizeEntry(Path.Combine("Media", "Audio"), "Audio", 50)
        ]);
    var now = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [],
        [],
        [
            new FolderSizeEntry("Media", "Media", 160),
            new FolderSizeEntry(Path.Combine("Media", "Video"), "Video", 150),
            new FolderSizeEntry(Path.Combine("Media", "Audio"), "Audio", 10),
            new FolderSizeEntry("New", "New", 30)
        ]);

    var comparison = new SnapshotComparer().Compare(before, now);
    AssertEqual(10, Find(comparison.Root, "Media").DeltaBytes, "Aggregate media delta");
    AssertEqual(50, Find(Find(comparison.Root, "Media"), "Video").DeltaBytes, "Aggregate video delta");
    AssertEqual(30, Find(comparison.Root, "New").DeltaBytes, "Aggregate new delta");
}

static void SnapshotComparerUsesAggregateRootTotalWithoutFolders()
{
    var before = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [],
        [],
        [],
        42,
        2);
    var now = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [],
        [],
        [],
        50,
        3);

    var comparison = new SnapshotComparer().Compare(before, now);
    AssertEqual(42L, comparison.Snapshot.TotalBytes, "Aggregate root-only snapshot total");
    AssertEqual(2, comparison.Snapshot.FileCount, "Aggregate root-only snapshot file count");
    AssertEqual(8L, comparison.DeltaBytes, "Aggregate root-only delta");
}

static void SnapshotComparerMixesLegacyFilesAndFolderAggregates()
{
    var before = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [new FileEntry(Path.Combine("Media", "Video", "old.bin"), 100, DateTime.UtcNow)],
        []);
    var now = new Snapshot(
        "T:\\",
        "Test",
        "NTFS",
        DateTime.UtcNow,
        [],
        [],
        [new FolderSizeEntry(Path.Combine("Media", "Video"), "Video", 150), new FolderSizeEntry("Media", "Media", 150)],
        150);

    var comparison = new SnapshotComparer().Compare(before, now);
    AssertEqual(50, comparison.DeltaBytes, "Mixed root delta");
    AssertEqual(50, Find(Find(comparison.Root, "Media"), "Video").DeltaBytes, "Mixed video delta");
}

static void DirectorySnapshotStoresAggregateFolderSizes()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var media = Path.Combine(tempRoot, "Media");
        var video = Path.Combine(media, "Video");
        Directory.CreateDirectory(video);
        File.WriteAllBytes(Path.Combine(tempRoot, "root.bin"), new byte[5]);
        File.WriteAllBytes(Path.Combine(media, "cover.jpg"), new byte[10]);
        File.WriteAllBytes(Path.Combine(video, "clip.mp4"), new byte[90]);

        var snapshot = SnapshotBuilder.CreateDirectorySnapshot(tempRoot, progress: null, CancellationToken.None);

        AssertEqual(0, snapshot.Files.Count, "Directory snapshot omits file entries");
        AssertEqual(3, snapshot.FileCount, "Directory snapshot file count");
        AssertEqual(105L, snapshot.TotalBytes, "Directory snapshot total bytes");
        AssertEqual(100L, snapshot.FolderSizes.Single(folder => folder.RelativePath == "Media").Size, "Directory parent folder size");
        AssertEqual(90L, snapshot.FolderSizes.Single(folder => folder.RelativePath == Path.Combine("Media", "Video")).Size, "Directory child folder size");
    }
    finally
    {
        DeleteOwnedTempDirectoryContents(tempRoot);
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void ApplicationUpdaterPrefersPortableArchiveOutsideProgramFiles()
{
    var release = new ReleaseInfo(
        "v1.2.3",
        "DiskCompare v1.2.3",
        new Uri("https://example.test/releases/v1.2.3"),
        new Version(1, 2, 3),
        [
            new ReleaseAsset("DiskCompare-v1.2.3-win-x64.exe", new Uri("https://example.test/app.exe"), 1),
            new ReleaseAsset("DiskCompare-v1.2.3-win-x64.msi", new Uri("https://example.test/app.msi"), 1),
            new ReleaseAsset("DiskCompare-v1.2.3-win-x64-portable.zip", new Uri("https://example.test/app.zip"), 1)
        ]);

    var selected = new ApplicationUpdater().SelectPreferredAsset(release)
        ?? throw new InvalidOperationException("Expected updater to select an asset.");

    AssertEqual("DiskCompare-v1.2.3-win-x64-portable.zip", selected.Name, "Portable asset");
}

static void ApplicationUpdaterRecognizesPortableArchivePackage()
{
    var asset = new ReleaseAsset("DiskCompare-v1.2.3-win-x64-portable.zip", new Uri("https://example.test/app.zip"), 1);

    AssertEqual(UpdatePackageKind.PortableArchive, ApplicationUpdater.GetPackageKind(asset), "Portable archive package kind");
}

static void ApplicationUpdaterRecognizesExecutableInstallerPackage()
{
    var asset = new ReleaseAsset("DiskCompare-v1.2.3-win-x64.exe", new Uri("https://example.test/app.exe"), 1);

    AssertEqual(UpdatePackageKind.ExecutableInstaller, ApplicationUpdater.GetPackageKind(asset), "Executable installer package kind");
}

static void RuntimeRequirementDetectsRequiredDesktopRuntimeMajorVersion()
{
    AssertTrue(
        RuntimeRequirement.HasWindowsDesktopRuntimeMajorVersion(["7.0.5", "8.0.24", "10.0.1"], RuntimeRequirement.RequiredWindowsDesktopRuntimeMajorVersion),
        "Required desktop runtime should be detected");
}

static void RuntimeRequirementRejectsMissingDesktopRuntimeMajorVersion()
{
    AssertFalse(
        RuntimeRequirement.HasWindowsDesktopRuntimeMajorVersion(["6.0.36", "7.0.20", "10.0.1"], RuntimeRequirement.RequiredWindowsDesktopRuntimeMajorVersion),
        "Missing desktop runtime should be rejected");
}

static void NtfsIndexCacheStoresUniqueFilesAndLoadsNewestUsn()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var store = new NtfsIndexCacheStore(tempRoot);
        var older = CreateCache(nextUsn: 100);
        var newer = CreateCache(nextUsn: 300);

        store.Save(older);
        store.Save(CreateCache(nextUsn: 150));
        store.Save(CreateCache(nextUsn: 200));
        store.Save(CreateCache(nextUsn: 250));
        store.Save(newer);

        var files = Directory.EnumerateFiles(tempRoot, "*.ntfsindex").ToArray();
        AssertEqual(3, files.Length, "Cache files are pruned");

        var loaded = store.TryLoad("T:\\", 0x1234ABCD)
            ?? throw new InvalidOperationException("Expected cache to load.");
        AssertEqual(300L, loaded.NextUsn, "Newest cache USN");
        AssertEqual("sample.bin", loaded.Records[0].Names[0].Name, "Cache name");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void NtfsIndexCacheIgnoresMaliciousCounts()
{
    var tempRoot = CreateOwnedTempDirectory();

    try
    {
        var path = Path.Combine(tempRoot, "T-1234ABCD-0000000000000001-malicious.ntfsindex");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(output))
        {
            writer.Write("DCNTFSIDX");
            writer.Write(NtfsIndexCache.CurrentSchemaVersion);
            writer.Write("T:\\");
            writer.Write("NTFS");
            writer.Write(0x1234ABCD);
            writer.Write((ulong)99);
            writer.Write(1L);
            writer.Write(1L);
            writer.Write(DateTime.UtcNow.Ticks);
            writer.Write(int.MaxValue);
        }

        var loaded = new NtfsIndexCacheStore(tempRoot).TryLoad("T:\\", 0x1234ABCD);
        AssertEqual<NtfsIndexCache?>(null, loaded, "Malicious cache ignored");
    }
    finally
    {
        DeleteOwnedTempDirectory(tempRoot);
    }
}

static void NtfsIndexCacheFactoryPreservesRecordData()
{
    var cache = CreateCache(nextUsn: 300);
    var rebuilt = NtfsMftSnapshotProvider.CreateIndexCacheForTest(cache);

    AssertEqual(cache.DriveRoot, rebuilt.DriveRoot, "Rebuilt cache drive");
    AssertEqual(cache.FileSystem, rebuilt.FileSystem, "Rebuilt cache file system");
    AssertEqual(cache.VolumeSerialNumber, rebuilt.VolumeSerialNumber, "Rebuilt cache serial");
    AssertEqual(cache.UsnJournalId, rebuilt.UsnJournalId, "Rebuilt cache journal");
    AssertEqual(cache.NextUsn, rebuilt.NextUsn, "Rebuilt cache next USN");
    AssertEqual(cache.Records.Length, rebuilt.Records.Length, "Rebuilt cache record count");
    AssertEqual(cache.Records[0].RecordNumber, rebuilt.Records[0].RecordNumber, "Rebuilt cache record number");
    AssertEqual(cache.Records[0].Names[0].Name, rebuilt.Records[0].Names[0].Name, "Rebuilt cache name");
}

static void NtfsMftAggregateSnapshotRollsFolderSizesUpward()
{
    var now = DateTime.UtcNow;
    var snapshot = NtfsMftSnapshotProvider.CreateSnapshotFromIndexCache(new NtfsIndexCache(
        NtfsIndexCache.CurrentSchemaVersion,
        "T:\\",
        "NTFS",
        0x1234ABCD,
        99,
        300,
        1,
        now,
        [
            new NtfsCachedRecord(
                5,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, ".", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                24,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, "Media", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                25,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(24, "Video", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                26,
                IsDirectory: false,
                DataSize: 10,
                FileNameSize: 10,
                [new NtfsCachedName(24, "cover.jpg", 1, FileAttributes.Archive, now, 10)]),
            new NtfsCachedRecord(
                27,
                IsDirectory: false,
                DataSize: 90,
                FileNameSize: 90,
                [new NtfsCachedName(25, "clip.mp4", 1, FileAttributes.Archive, now, 90)])
        ]));

    AssertEqual(0, snapshot.Files.Count, "Aggregate MFT snapshot omits file entries");
    AssertEqual(2, snapshot.FileCount, "Aggregate MFT file count");
    AssertEqual(100L, snapshot.TotalBytes, "Aggregate MFT total bytes");
    AssertEqual(100L, snapshot.FolderSizes.Single(folder => folder.RelativePath == "Media").Size, "Parent folder size");
    AssertEqual(90L, snapshot.FolderSizes.Single(folder => folder.RelativePath == Path.Combine("Media", "Video")).Size, "Child folder size");
}

static void NtfsMftAggregateSnapshotIgnoresOrphanFiles()
{
    var now = DateTime.UtcNow;
    var snapshot = NtfsMftSnapshotProvider.CreateSnapshotFromIndexCache(new NtfsIndexCache(
        NtfsIndexCache.CurrentSchemaVersion,
        "T:\\",
        "NTFS",
        0x1234ABCD,
        99,
        300,
        1,
        now,
        [
            new NtfsCachedRecord(
                5,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, ".", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                24,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, "Media", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                25,
                IsDirectory: false,
                DataSize: 10,
                FileNameSize: 10,
                [new NtfsCachedName(24, "cover.jpg", 1, FileAttributes.Archive, now, 10)]),
            new NtfsCachedRecord(
                26,
                IsDirectory: false,
                DataSize: 90,
                FileNameSize: 90,
                [new NtfsCachedName(9999, "orphan.bin", 1, FileAttributes.Archive, now, 90)])
        ]));

    AssertEqual(1, snapshot.FileCount, "Reachable MFT file count");
    AssertEqual(10L, snapshot.TotalBytes, "Reachable MFT total bytes");
    AssertEqual(10L, snapshot.FolderSizes.Single(folder => folder.RelativePath == "Media").Size, "Reachable folder size");
}

static void NtfsMftAggregateSnapshotKeepsPreferredNamesAndSkipsReparsePoints()
{
    var now = DateTime.UtcNow;
    var snapshot = NtfsMftSnapshotProvider.CreateSnapshotFromIndexCache(new NtfsIndexCache(
        NtfsIndexCache.CurrentSchemaVersion,
        "T:\\",
        "NTFS",
        0x1234ABCD,
        99,
        300,
        1,
        now,
        [
            new NtfsCachedRecord(
                5,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, ".", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                24,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [
                    new NtfsCachedName(5, "MEDIA~1", 2, FileAttributes.Directory, now, 0),
                    new NtfsCachedName(5, "Media Library", 1, FileAttributes.Directory, now, 0)
                ]),
            new NtfsCachedRecord(
                25,
                IsDirectory: false,
                DataSize: 10,
                FileNameSize: 10,
                [
                    new NtfsCachedName(24, "TRACK~1.MP3", 2, FileAttributes.Archive, now, 10),
                    new NtfsCachedName(24, "track.mp3", 1, FileAttributes.Archive, now, 10)
                ]),
            new NtfsCachedRecord(
                26,
                IsDirectory: false,
                DataSize: 90,
                FileNameSize: 90,
                [new NtfsCachedName(24, "linked.bin", 1, FileAttributes.Archive | FileAttributes.ReparsePoint, now, 90)])
        ]));

    AssertEqual(1, snapshot.FileCount, "Preferred MFT name file count");
    AssertEqual(10L, snapshot.TotalBytes, "Preferred MFT name total bytes");
    AssertEqual(10L, snapshot.FolderSizes.Single(folder => folder.RelativePath == "Media Library").Size, "Preferred directory name size");
}

static void NtfsMftAggregateSnapshotAcceptsUnnamedFileRecords()
{
    var now = DateTime.UtcNow;
    var snapshot = NtfsMftSnapshotProvider.CreateSnapshotFromIndexCache(new NtfsIndexCache(
        NtfsIndexCache.CurrentSchemaVersion,
        "T:\\",
        "NTFS",
        0x1234ABCD,
        99,
        300,
        1,
        now,
        [
            new NtfsCachedRecord(
                5,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, ".", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                24,
                IsDirectory: true,
                DataSize: 0,
                FileNameSize: 0,
                [new NtfsCachedName(5, "Media", 1, FileAttributes.Directory, now, 0)]),
            new NtfsCachedRecord(
                25,
                IsDirectory: false,
                DataSize: 42,
                FileNameSize: 42,
                [new NtfsCachedName(24, string.Empty, 1, FileAttributes.Archive, now, 42)])
        ]));

    AssertEqual(1, snapshot.FileCount, "Unnamed MFT file count");
    AssertEqual(42L, snapshot.TotalBytes, "Unnamed MFT total bytes");
    AssertEqual(42L, snapshot.FolderSizes.Single(folder => folder.RelativePath == "Media").Size, "Unnamed MFT folder size");
}

static NtfsIndexCache CreateCache(long nextUsn)
{
    return new NtfsIndexCache(
        NtfsIndexCache.CurrentSchemaVersion,
        "T:\\",
        "NTFS",
        0x1234ABCD,
        99,
        nextUsn,
        1,
        DateTime.UtcNow,
        [
            new NtfsCachedRecord(
                42,
                IsDirectory: false,
                DataSize: 10,
                FileNameSize: 10,
                [new NtfsCachedName(5, "sample.bin", 1, FileAttributes.Archive, DateTime.UtcNow, 10)])
        ]);
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

static void AssertTrue(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{label}: expected true, got false.");
    }
}

static void AssertFalse(bool condition, string label)
{
    if (condition)
    {
        throw new InvalidOperationException($"{label}: expected false, got true.");
    }
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

static void DeleteOwnedTempDirectoryContents(string path)
{
    var fullPath = Path.GetFullPath(path);
    var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DiskCompare.Tests"));
    if (!IsPathInsideDirectory(fullPath, allowedRoot))
    {
        throw new InvalidOperationException($"Refusing to clean path outside owned test temp directory: {fullPath}");
    }

    if (!Directory.Exists(fullPath))
    {
        return;
    }

    foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
    {
        File.Delete(file);
    }

    foreach (var directory in Directory.EnumerateDirectories(fullPath, "*", SearchOption.AllDirectories).OrderByDescending(static item => item.Length))
    {
        Directory.Delete(directory, recursive: false);
    }
}

static bool IsPathInsideDirectory(string path, string directory)
{
    var normalizedDirectory = Path.EndsInDirectorySeparator(directory)
        ? directory
        : directory + Path.DirectorySeparatorChar;

    return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
}
