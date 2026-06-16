using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskCompare.Core.Snapshots;

internal sealed class NtfsMftSnapshotProvider
{
    private const int MftRecordSignature = 0x454C4946; // FILE
    private const int AttributeTypeFileName = 0x30;
    private const int AttributeTypeData = 0x80;
    private const int AttributeTypeEnd = -1;
    private const long RootDirectoryRecordNumber = 5;
    private const long FirstUserRecordNumber = 24;
    private const int ProgressRecordInterval = 65_536;
    private const int RecordsPerChunk = 8_192;
    private const int UsnReadBufferSize = 8 * 1024 * 1024;
    private const ulong FileReferenceMask = 0x0000FFFFFFFFFFFF;

    public Snapshot? TryCreate(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken,
        out string? fallbackReason)
    {
        fallbackReason = null;

        if (!OperatingSystem.IsWindows())
        {
            fallbackReason = "NTFS fast index is only available on Windows.";
            return null;
        }

        try
        {
            return Create(driveRoot, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            fallbackReason = ex.Message;
            return null;
        }
    }

    internal static Snapshot CreateSnapshotFromIndexCache(NtfsIndexCache cache)
    {
        var indexedSnapshot = BuildIndexedSnapshot(ToEntries(cache));
        return new Snapshot(
            EnsureTrailingSeparator(cache.DriveRoot),
            string.Empty,
            cache.FileSystem,
            cache.UpdatedAtUtc,
            [],
            [],
            indexedSnapshot.Folders,
            indexedSnapshot.TotalBytes,
            indexedSnapshot.FileCount);
    }

    internal static NtfsIndexCache CreateIndexCacheForTest(NtfsIndexCache cache)
    {
        return CreateCache(
            cache.DriveRoot,
            cache.FileSystem,
            cache.VolumeSerialNumber,
            new UsnJournalData(cache.UsnJournalId, cache.LowestValidUsn, cache.NextUsn, cache.LowestValidUsn),
            ToEntries(cache));
    }

    private static Snapshot Create(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var drive = new DriveInfo(driveRoot);
        var format = SafeGet(() => drive.DriveFormat);
        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"The selected volume uses {format}, not NTFS.");
        }

        var volumePath = ToVolumePath(driveRoot);
        using var handle = NativeMethods.CreateFile(
            volumePath,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagSequentialScan,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to open {volumePath} for read-only NTFS indexing.");
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1024 * 1024, isAsync: false);
        var boot = ReadBootSector(stream);
        var mftMap = ReadMftDataMap(stream, boot);
        var recordCount = mftMap.InitializedSize / boot.FileRecordSize;
        if (recordCount <= FirstUserRecordNumber)
        {
            throw new InvalidDataException("The NTFS MFT does not contain user records.");
        }

        var volumeSerialNumber = TryGetVolumeSerialNumber(driveRoot);
        var journal = TryQueryUsnJournal(handle);
        if (volumeSerialNumber is not null && journal is not null)
        {
            var usnSnapshot = TryCreateFromUsnCache(
                stream,
                boot,
                mftMap,
                recordCount,
                driveRoot,
                format,
                volumeSerialNumber.Value,
                journal,
                progress,
                cancellationToken);

            if (usnSnapshot is not null)
            {
                return usnSnapshot;
            }
        }

        progress?.Report(new SnapshotProgress("正在读取 NTFS MFT 元数据...", 0, 0, 0, "NTFS MFT 快速索引"));
        var entries = ReadRecords(stream, boot, mftMap, recordCount, progress, cancellationToken);
        var indexedSnapshot = BuildIndexedSnapshot(entries);
        if (volumeSerialNumber is not null && journal is not null)
        {
            QueueSaveCache(driveRoot, format, volumeSerialNumber.Value, journal, entries, progress);
        }

        progress?.Report(new SnapshotProgress("NTFS MFT 快速索引完成", indexedSnapshot.FileCount, indexedSnapshot.TotalBytes, 0, "NTFS MFT 快速索引"));

        return new Snapshot(
            EnsureTrailingSeparator(driveRoot),
            SafeGet(() => drive.VolumeLabel),
            format,
            DateTime.UtcNow,
            [],
            [],
            indexedSnapshot.Folders,
            indexedSnapshot.TotalBytes,
            indexedSnapshot.FileCount);
    }

    private static Snapshot? TryCreateFromUsnCache(
        FileStream stream,
        NtfsBootSector boot,
        MftDataMap mftMap,
        long recordCount,
        string driveRoot,
        string fileSystem,
        uint volumeSerialNumber,
        UsnJournalData journal,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var cacheStore = new NtfsIndexCacheStore();
            var cache = cacheStore.TryLoad(driveRoot, volumeSerialNumber);
            if (cache is null || !IsUsnCacheUsable(cache, driveRoot, fileSystem, volumeSerialNumber, journal))
            {
                return null;
            }

            progress?.Report(new SnapshotProgress("正在读取 NTFS USN 增量日志...", 0, 0, 0, "NTFS USN 增量索引"));
            var entries = ToEntries(cache);
            var changedRecordNumbers = ReadChangedRecordNumbers(
                stream.SafeFileHandle,
                cache.NextUsn,
                journal.NextUsn,
                journal.UsnJournalId,
                progress,
                cancellationToken);

            foreach (var changedRecordNumber in changedRecordNumbers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = TryReadRecord(stream, boot, mftMap, recordCount, changedRecordNumber);
                if (entry is null)
                {
                    entries.Remove(changedRecordNumber);
                    continue;
                }

                entries[changedRecordNumber] = entry;
            }

            var indexedSnapshot = BuildIndexedSnapshot(entries);
            QueueSaveCache(driveRoot, fileSystem, volumeSerialNumber, journal, entries, progress);
            progress?.Report(new SnapshotProgress("NTFS USN 增量索引完成", indexedSnapshot.FileCount, indexedSnapshot.TotalBytes, 0, "NTFS USN 增量索引"));

            return new Snapshot(
                EnsureTrailingSeparator(driveRoot),
                SafeGet(() => new DriveInfo(driveRoot).VolumeLabel),
                fileSystem,
                DateTime.UtcNow,
                [],
                [],
                indexedSnapshot.Folders,
                indexedSnapshot.TotalBytes,
                indexedSnapshot.FileCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            progress?.Report(new SnapshotProgress($"NTFS USN 增量索引不可用，回退完整 MFT: {ex.Message}", 0, 0, 0, "NTFS MFT 快速索引"));
            return null;
        }
    }

    private static NtfsBootSector ReadBootSector(FileStream stream)
    {
        Span<byte> sector = stackalloc byte[512];
        ReadExactlyAt(stream, sector, 0);

        var oemId = Encoding.ASCII.GetString(sector.Slice(3, 8));
        if (!string.Equals(oemId, "NTFS    ", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The selected volume is not NTFS.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0x0B, 2));
        var sectorsPerCluster = sector[0x0D];
        if (bytesPerSector == 0 || sectorsPerCluster == 0)
        {
            throw new InvalidDataException("The NTFS boot sector contains an invalid cluster size.");
        }

        var bytesPerCluster = bytesPerSector * sectorsPerCluster;
        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(0x30, 8));
        var clustersPerFileRecord = unchecked((sbyte)sector[0x40]);
        var fileRecordSize = clustersPerFileRecord > 0
            ? bytesPerCluster * clustersPerFileRecord
            : 1 << Math.Abs(clustersPerFileRecord);

        if (mftCluster <= 0 || fileRecordSize <= 0)
        {
            throw new InvalidDataException("The NTFS boot sector contains an invalid MFT location.");
        }

        return new NtfsBootSector(bytesPerSector, bytesPerCluster, mftCluster, fileRecordSize);
    }

    private static MftDataMap ReadMftDataMap(FileStream stream, NtfsBootSector boot)
    {
        var record = new byte[boot.FileRecordSize];
        ReadExactlyAt(stream, record, boot.MftByteOffset);
        if (!ApplyFixup(record, boot.BytesPerSector))
        {
            throw new InvalidDataException("The $MFT file record has an invalid update sequence.");
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14, 2));
        var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x18, 4));

        for (var offset = (int)firstAttributeOffset; offset + 16 < usedSize && offset + 16 < record.Length;)
        {
            var attribute = record.AsSpan(offset);
            var type = BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(0, 4));
            if (type == AttributeTypeEnd)
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(4, 4));
            if (length <= 0 || offset + length > record.Length)
            {
                break;
            }

            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];
            if (type == AttributeTypeData && nonResident && nameLength == 0)
            {
                var runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(0x20, 2));
                if (runListOffset <= 0 || runListOffset >= length)
                {
                    throw new InvalidDataException("The $MFT data run list offset is invalid.");
                }

                var dataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(0x30, 8));
                var initializedSize = BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(0x38, 8));
                var runs = ParseDataRuns(attribute.Slice(runListOffset, length - runListOffset));
                if (runs.Count == 0 || initializedSize <= 0)
                {
                    throw new InvalidDataException("The $MFT data attribute does not contain readable extents.");
                }

                return new MftDataMap(runs, dataSize, initializedSize);
            }

            offset += length;
        }

        throw new InvalidDataException("Unable to locate the non-resident $MFT data attribute.");
    }

    private static Dictionary<long, NtfsRecordEntry> ReadRecords(
        FileStream stream,
        NtfsBootSector boot,
        MftDataMap mftMap,
        long recordCount,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var capacity = (int)Math.Min(recordCount, 1_000_000);
        var entries = new Dictionary<long, NtfsRecordEntry>(capacity);
        var chunkRecordCapacity = Math.Max(1, Math.Min(RecordsPerChunk, 32 * 1024 * 1024 / boot.FileRecordSize));
        var buffer = new byte[chunkRecordCapacity * boot.FileRecordSize];
        long recordNumber = 0;

        foreach (var run in mftMap.Runs)
        {
            var recordsInRun = (run.ClusterCount * boot.BytesPerCluster) / boot.FileRecordSize;
            if (recordsInRun <= 0)
            {
                continue;
            }

            if (run.LogicalClusterNumber < 0)
            {
                recordNumber += recordsInRun;
                continue;
            }

            var recordsRemaining = Math.Min(recordsInRun, recordCount - recordNumber);
            long offsetInRunRecords = 0;

            while (recordsRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var recordsToRead = (int)Math.Min(chunkRecordCapacity, recordsRemaining);
                var bytesToRead = recordsToRead * boot.FileRecordSize;
                var diskOffset = (run.LogicalClusterNumber * boot.BytesPerCluster) + (offsetInRunRecords * boot.FileRecordSize);
                ReadExactlyAt(stream, buffer.AsSpan(0, bytesToRead), diskOffset);

                for (var index = 0; index < recordsToRead; index++)
                {
                    var currentRecordNumber = recordNumber + index;
                    var record = buffer.AsSpan(index * boot.FileRecordSize, boot.FileRecordSize);
                    var entry = TryParseRecord(record, currentRecordNumber, boot.BytesPerSector);
                    if (entry is not null)
                    {
                        entries[currentRecordNumber] = entry;
                    }
                }

                recordNumber += recordsToRead;
                offsetInRunRecords += recordsToRead;
                recordsRemaining -= recordsToRead;

                if (recordNumber % ProgressRecordInterval == 0)
                {
                    progress?.Report(new SnapshotProgress($"正在读取 NTFS MFT 记录 {recordNumber:N0}/{recordCount:N0}", entries.Count, 0, 0, "NTFS MFT 快速索引"));
                }
            }

            if (recordNumber >= recordCount)
            {
                break;
            }
        }

        return entries;
    }

    private static NtfsRecordEntry? TryParseRecord(Span<byte> record, long recordNumber, int bytesPerSector)
    {
        if (record.Length < 64 || BinaryPrimitives.ReadInt32LittleEndian(record.Slice(0, 4)) != MftRecordSignature)
        {
            return null;
        }

        if (!ApplyFixup(record, bytesPerSector))
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x16, 2));
        var inUse = (flags & 0x0001) != 0;
        if (!inUse)
        {
            return null;
        }

        var entry = new NtfsRecordEntry(recordNumber, isDirectory: (flags & 0x0002) != 0);
        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x14, 2));
        var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0x18, 4));

        for (var offset = (int)firstAttributeOffset; offset + 16 < usedSize && offset + 16 < record.Length;)
        {
            var attribute = record.Slice(offset);
            var type = BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(0, 4));
            if (type == AttributeTypeEnd)
            {
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(4, 4));
            if (length <= 0 || offset + length > record.Length)
            {
                break;
            }

            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];

            if (type == AttributeTypeFileName && !nonResident)
            {
                var value = GetResidentValue(attribute, length);
                var fileName = TryParseFileName(value);
                if (fileName is not null)
                {
                    entry.Names.Add(fileName);
                    entry.FileNameSize = Math.Max(entry.FileNameSize, fileName.RealSize);
                }
            }
            else if (type == AttributeTypeData && nameLength == 0 && !entry.IsDirectory)
            {
                entry.DataSize = Math.Max(entry.DataSize, GetDataSize(attribute, length, nonResident));
            }

            offset += length;
        }

        return entry.Names.Count == 0 ? null : entry;
    }

    private static NtfsRecordEntry? TryReadRecord(
        FileStream stream,
        NtfsBootSector boot,
        MftDataMap mftMap,
        long recordCount,
        long recordNumber)
    {
        if (recordNumber < 0 || recordNumber >= recordCount)
        {
            return null;
        }

        var remainingRecordOffset = recordNumber;
        foreach (var run in mftMap.Runs)
        {
            var recordsInRun = (run.ClusterCount * boot.BytesPerCluster) / boot.FileRecordSize;
            if (recordsInRun <= 0)
            {
                continue;
            }

            if (remainingRecordOffset >= recordsInRun)
            {
                remainingRecordOffset -= recordsInRun;
                continue;
            }

            if (run.LogicalClusterNumber < 0)
            {
                return null;
            }

            var buffer = new byte[boot.FileRecordSize];
            var diskOffset = (run.LogicalClusterNumber * boot.BytesPerCluster) + (remainingRecordOffset * boot.FileRecordSize);
            ReadExactlyAt(stream, buffer, diskOffset);
            return TryParseRecord(buffer, recordNumber, boot.BytesPerSector);
        }

        return null;
    }

    private static IndexedSnapshot BuildIndexedSnapshot(Dictionary<long, NtfsRecordEntry> entries)
    {
        var directDirectorySizes = new Dictionary<long, long>();
        var directDirectoryFileCounts = new Dictionary<long, int>();
        var directoryChildren = new Dictionary<long, List<long>>();
        var directoryPathCache = new Dictionary<long, string?> { [RootDirectoryRecordNumber] = string.Empty };

        foreach (var entry in entries.Values)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            var parentRecordNumber = entry.GetPreferredVisibleName()?.ParentRecordNumber;
            if (parentRecordNumber is null || parentRecordNumber.Value == entry.RecordNumber)
            {
                continue;
            }

            if (!directoryChildren.TryGetValue(parentRecordNumber.Value, out var children))
            {
                children = [];
                directoryChildren[parentRecordNumber.Value] = children;
            }

            children.Add(entry.RecordNumber);
        }

        foreach (var entry in entries.Values)
        {
            if (entry.RecordNumber < FirstUserRecordNumber || entry.IsDirectory)
            {
                continue;
            }

            foreach (var name in entry.GetVisibleNames())
            {
                if (name.IsReparsePoint)
                {
                    continue;
                }

                directDirectorySizes[name.ParentRecordNumber] = directDirectorySizes.GetValueOrDefault(name.ParentRecordNumber) + entry.Size;
                directDirectoryFileCounts[name.ParentRecordNumber] = directDirectoryFileCounts.GetValueOrDefault(name.ParentRecordNumber) + 1;
            }
        }

        var folderSizes = BuildFolderSizes(entries, directDirectorySizes, directoryChildren, directoryPathCache);
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var (directoryRecordNumber, size) in directDirectorySizes)
        {
            if (!IsReachableDirectory(directoryRecordNumber, directoryPathCache))
            {
                continue;
            }

            totalBytes += size;
            fileCount += directDirectoryFileCounts.GetValueOrDefault(directoryRecordNumber);
        }

        return new IndexedSnapshot(ToFolderEntries(folderSizes), totalBytes, fileCount);
    }

    private static bool IsReachableDirectory(long directoryRecordNumber, Dictionary<long, string?> directoryPathCache)
    {
        return directoryPathCache.TryGetValue(directoryRecordNumber, out var path) && path is not null;
    }

    private static Dictionary<string, FolderSizeEntryBuilder> BuildFolderSizes(
        Dictionary<long, NtfsRecordEntry> entries,
        Dictionary<long, long> directDirectorySizes,
        Dictionary<long, List<long>> directoryChildren,
        Dictionary<long, string?> directoryPathCache)
    {
        var folderSizes = new Dictionary<string, FolderSizeEntryBuilder>(StringComparer.OrdinalIgnoreCase);
        var subtreeSizeCache = new Dictionary<long, long>();
        var visiting = new HashSet<long>();

        foreach (var directoryRecordNumber in directDirectorySizes.Keys)
        {
            if (directoryRecordNumber == RootDirectoryRecordNumber || directoryPathCache.ContainsKey(directoryRecordNumber))
            {
                continue;
            }

            if (!entries.TryGetValue(directoryRecordNumber, out var entry) || !entry.IsDirectory)
            {
                continue;
            }

            visiting.Clear();
            _ = ResolveDirectoryPath(directoryRecordNumber, entries, directoryPathCache, visiting);
        }

        foreach (var (directoryRecordNumber, path) in directoryPathCache)
        {
            if (directoryRecordNumber == RootDirectoryRecordNumber || string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (!entries.TryGetValue(directoryRecordNumber, out var entry) || !entry.IsDirectory)
            {
                continue;
            }

            var size = GetSubtreeSize(directoryRecordNumber, directDirectorySizes, directoryChildren, subtreeSizeCache, visiting);
            if (size > 0)
            {
                folderSizes[path] = new FolderSizeEntryBuilder(path, GetName(path)) { Size = size };
            }
        }

        return folderSizes;
    }

    private static long GetSubtreeSize(
        long directoryRecordNumber,
        Dictionary<long, long> directDirectorySizes,
        Dictionary<long, List<long>> directoryChildren,
        Dictionary<long, long> cache,
        HashSet<long> visiting)
    {
        if (cache.TryGetValue(directoryRecordNumber, out var cached))
        {
            return cached;
        }

        if (!visiting.Add(directoryRecordNumber))
        {
            return 0;
        }

        var size = directDirectorySizes.GetValueOrDefault(directoryRecordNumber);
        if (directoryChildren.TryGetValue(directoryRecordNumber, out var children))
        {
            foreach (var child in children)
            {
                size += GetSubtreeSize(child, directDirectorySizes, directoryChildren, cache, visiting);
            }
        }

        visiting.Remove(directoryRecordNumber);
        cache[directoryRecordNumber] = size;
        return size;
    }

    private static bool IsUsnCacheUsable(
        NtfsIndexCache cache,
        string driveRoot,
        string fileSystem,
        uint volumeSerialNumber,
        UsnJournalData journal)
    {
        return cache.SchemaVersion == NtfsIndexCache.CurrentSchemaVersion
            && string.Equals(EnsureTrailingSeparator(cache.DriveRoot), EnsureTrailingSeparator(driveRoot), StringComparison.OrdinalIgnoreCase)
            && string.Equals(cache.FileSystem, fileSystem, StringComparison.OrdinalIgnoreCase)
            && cache.VolumeSerialNumber == volumeSerialNumber
            && cache.UsnJournalId == journal.UsnJournalId
            && cache.NextUsn >= journal.LowestValidUsn
            && cache.NextUsn <= journal.NextUsn;
    }

    private static Dictionary<long, NtfsRecordEntry> ToEntries(NtfsIndexCache cache)
    {
        var entries = new Dictionary<long, NtfsRecordEntry>(cache.Records.Length);
        foreach (var cachedRecord in cache.Records)
        {
            var entry = new NtfsRecordEntry(cachedRecord.RecordNumber, cachedRecord.IsDirectory)
            {
                DataSize = cachedRecord.DataSize,
                FileNameSize = cachedRecord.FileNameSize
            };

            foreach (var cachedName in cachedRecord.Names)
            {
                entry.Names.Add(new NtfsFileName(
                    cachedName.ParentRecordNumber,
                    cachedName.Name,
                    cachedName.NamespaceId,
                    cachedName.Attributes,
                    cachedName.LastWriteTimeUtc,
                    cachedName.RealSize));
            }

            entries[entry.RecordNumber] = entry;
        }

        return entries;
    }

    private static void QueueSaveCache(
        string driveRoot,
        string fileSystem,
        uint volumeSerialNumber,
        UsnJournalData journal,
        Dictionary<long, NtfsRecordEntry> entries,
        IProgress<SnapshotProgress>? progress)
    {
        try
        {
            var cache = CreateCache(driveRoot, fileSystem, volumeSerialNumber, journal, entries);
            _ = Task.Run(() => SaveCache(cache, progress));
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            progress?.Report(new SnapshotProgress($"NTFS 索引缓存准备失败，已跳过: {ex.Message}", 0, 0, 0, "NTFS MFT 快速索引"));
        }
    }

    private static NtfsIndexCache CreateCache(
        string driveRoot,
        string fileSystem,
        uint volumeSerialNumber,
        UsnJournalData journal,
        Dictionary<long, NtfsRecordEntry> entries)
    {
        var records = new List<NtfsCachedRecord>(entries.Count);
        foreach (var entry in entries.Values)
        {
            var names = new NtfsCachedName[entry.Names.Count];
            for (var index = 0; index < names.Length; index++)
            {
                var name = entry.Names[index];
                names[index] = new NtfsCachedName(
                    name.ParentRecordNumber,
                    name.Name,
                    name.NamespaceId,
                    name.Attributes,
                    name.LastWriteTimeUtc,
                    name.RealSize);
            }

            records.Add(new NtfsCachedRecord(
                entry.RecordNumber,
                entry.IsDirectory,
                entry.DataSize,
                entry.FileNameSize,
                names));
        }

        return new NtfsIndexCache(
            NtfsIndexCache.CurrentSchemaVersion,
            EnsureTrailingSeparator(driveRoot),
            fileSystem,
            volumeSerialNumber,
            journal.UsnJournalId,
            journal.NextUsn,
            journal.LowestValidUsn,
            DateTime.UtcNow,
            records.ToArray());
    }

    private static void SaveCache(NtfsIndexCache cache, IProgress<SnapshotProgress>? progress)
    {
        try
        {
            new NtfsIndexCacheStore().Save(cache);
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            progress?.Report(new SnapshotProgress($"NTFS 索引缓存保存失败，已跳过: {ex.Message}", 0, 0, 0, "NTFS MFT 快速索引"));
        }
    }

    private static string? ResolveDirectoryPath(
        long recordNumber,
        Dictionary<long, NtfsRecordEntry> entries,
        Dictionary<long, string?> cache,
        HashSet<long> visiting)
    {
        if (cache.TryGetValue(recordNumber, out var cached))
        {
            return cached;
        }

        if (!visiting.Add(recordNumber))
        {
            cache[recordNumber] = null;
            return null;
        }

        if (!entries.TryGetValue(recordNumber, out var entry) || !entry.IsDirectory)
        {
            cache[recordNumber] = null;
            visiting.Remove(recordNumber);
            return null;
        }

        var name = entry.GetPreferredVisibleName();
        if (name is null || name.Name == ".")
        {
            cache[recordNumber] = string.Empty;
            visiting.Remove(recordNumber);
            return string.Empty;
        }

        var parentPath = ResolveDirectoryPath(name.ParentRecordNumber, entries, cache, visiting);
        var path = parentPath is null
            ? null
            : string.IsNullOrEmpty(parentPath) ? name.Name : string.Concat(parentPath, Path.DirectorySeparatorChar, name.Name);

        cache[recordNumber] = path;
        visiting.Remove(recordNumber);
        return path;
    }

    private static string GetName(string relativePath)
    {
        var separator = relativePath.LastIndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    private static FolderSizeEntry[] ToFolderEntries(Dictionary<string, FolderSizeEntryBuilder> folderSizes)
    {
        var folders = new FolderSizeEntry[folderSizes.Count];
        var index = 0;
        foreach (var folder in folderSizes.Values)
        {
            folders[index++] = new FolderSizeEntry(folder.RelativePath, folder.Name, folder.Size);
        }

        return folders;
    }

    private static ReadOnlySpan<byte> GetResidentValue(Span<byte> attribute, int attributeLength)
    {
        if (attributeLength < 0x18)
        {
            return [];
        }

        var valueLength = BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(0x10, 4));
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(0x14, 2));
        if (valueLength <= 0 || valueOffset < 0 || valueOffset + valueLength > attributeLength)
        {
            return [];
        }

        return attribute.Slice(valueOffset, valueLength);
    }

    private static NtfsFileName? TryParseFileName(ReadOnlySpan<byte> value)
    {
        if (value.Length < 66)
        {
            return null;
        }

        var nameLength = value[64];
        var namespaceId = value[65];
        var nameByteLength = nameLength * 2;
        if (nameLength == 0 || 66 + nameByteLength > value.Length)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(value.Slice(66, nameByteLength));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parentRecordNumber = (long)(BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0, 8)) & FileReferenceMask);
        var lastWriteTime = SafeFileTime(BinaryPrimitives.ReadInt64LittleEndian(value.Slice(16, 8)));
        var realSize = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(value.Slice(48, 8)));
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(56, 4));
        return new NtfsFileName(parentRecordNumber, name, namespaceId, attributes, lastWriteTime, realSize);
    }

    private static long GetDataSize(Span<byte> attribute, int attributeLength, bool nonResident)
    {
        if (nonResident)
        {
            return attributeLength >= 0x38
                ? Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(0x30, 8)))
                : 0;
        }

        return attributeLength >= 0x18
            ? Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(attribute.Slice(0x10, 4)))
            : 0;
    }

    private static bool ApplyFixup(Span<byte> record, int bytesPerSector)
    {
        var updateSequenceOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x04, 2));
        var updateSequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x06, 2));
        if (updateSequenceOffset <= 0 || updateSequenceCount <= 1)
        {
            return false;
        }

        var updateSequenceEnd = updateSequenceOffset + updateSequenceCount * 2;
        if (updateSequenceEnd > record.Length)
        {
            return false;
        }

        var updateSequenceNumber = record.Slice(updateSequenceOffset, 2);
        for (var sector = 1; sector < updateSequenceCount; sector++)
        {
            var sectorEnd = sector * bytesPerSector - 2;
            var replacementOffset = updateSequenceOffset + sector * 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length || replacementOffset + 2 > record.Length)
            {
                return false;
            }

            if (!record.Slice(sectorEnd, 2).SequenceEqual(updateSequenceNumber))
            {
                return false;
            }

            record[sectorEnd] = record[replacementOffset];
            record[sectorEnd + 1] = record[replacementOffset + 1];
        }

        return true;
    }

    private static UsnJournalData? TryQueryUsnJournal(SafeFileHandle handle)
    {
        try
        {
            Span<byte> output = stackalloc byte[80];
            if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.FsctlQueryUsnJournal,
                IntPtr.Zero,
                0,
                ref MemoryMarshal.GetReference(output),
                output.Length,
                out var bytesReturned,
                IntPtr.Zero)
                || bytesReturned < 32)
            {
                return null;
            }

            return new UsnJournalData(
                BinaryPrimitives.ReadUInt64LittleEndian(output.Slice(0, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(output.Slice(8, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(output.Slice(16, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(output.Slice(24, 8)));
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            return null;
        }
    }

    private static HashSet<long> ReadChangedRecordNumbers(
        SafeFileHandle handle,
        long startUsn,
        long targetUsn,
        ulong usnJournalId,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var changedRecordNumbers = new HashSet<long>();
        if (startUsn >= targetUsn)
        {
            return changedRecordNumbers;
        }

        var input = new ReadUsnJournalData(
            startUsn,
            NativeMethods.ReasonAll,
            returnOnlyOnClose: 0,
            timeout: 0,
            bytesToWaitFor: 0,
            usnJournalId);
        var inputSize = Marshal.SizeOf<ReadUsnJournalData>();
        var buffer = new byte[UsnReadBufferSize];

        while (input.StartUsn < targetUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            try
            {
                if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlReadUsnJournal,
                    inputHandle.AddrOfPinnedObject(),
                    inputSize,
                    buffer,
                    buffer.Length,
                    out var bytesReturned,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read NTFS USN journal.");
                }

                if (bytesReturned <= sizeof(long))
                {
                    break;
                }

                input.StartUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
                var offset = sizeof(long);
                while (offset + 8 <= bytesReturned)
                {
                    var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
                    if (recordLength < 60 || offset + recordLength > bytesReturned)
                    {
                        break;
                    }

                    var record = buffer.AsSpan(offset, (int)recordLength);
                    AddChangedRecordNumbers(record, changedRecordNumbers);
                    offset += (int)recordLength;
                }

                progress?.Report(new SnapshotProgress($"正在读取 NTFS USN 增量 {input.StartUsn:N0}/{targetUsn:N0}", changedRecordNumbers.Count, 0, 0, "NTFS USN 增量索引"));
            }
            finally
            {
                inputHandle.Free();
            }
        }

        return changedRecordNumbers;
    }

    private static void AddChangedRecordNumbers(ReadOnlySpan<byte> usnRecord, HashSet<long> changedRecordNumbers)
    {
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(usnRecord.Slice(4, 2));
        if (majorVersion == 2 && usnRecord.Length >= 60)
        {
            changedRecordNumbers.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(usnRecord.Slice(8, 8)) & FileReferenceMask));
            changedRecordNumbers.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(usnRecord.Slice(16, 8)) & FileReferenceMask));
        }
        else if (majorVersion == 3 && usnRecord.Length >= 76)
        {
            changedRecordNumbers.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(usnRecord.Slice(8, 8)) & FileReferenceMask));
            changedRecordNumbers.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(usnRecord.Slice(24, 8)) & FileReferenceMask));
        }
    }

    private static List<NtfsDataRun> ParseDataRuns(ReadOnlySpan<byte> runList)
    {
        var runs = new List<NtfsDataRun>();
        long currentLogicalClusterNumber = 0;
        for (var index = 0; index < runList.Length;)
        {
            var header = runList[index++];
            if (header == 0)
            {
                break;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || index + lengthSize + offsetSize > runList.Length)
            {
                break;
            }

            var clusterCount = ReadUnsignedInteger(runList.Slice(index, lengthSize));
            index += lengthSize;
            var logicalClusterNumber = -1L;
            if (offsetSize > 0)
            {
                var clusterOffset = ReadSignedInteger(runList.Slice(index, offsetSize));
                currentLogicalClusterNumber += clusterOffset;
                logicalClusterNumber = currentLogicalClusterNumber;
            }

            index += offsetSize;
            if (clusterCount <= 0)
            {
                break;
            }

            runs.Add(new NtfsDataRun(logicalClusterNumber, clusterCount));
        }

        return runs;
    }

    private static long ReadUnsignedInteger(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            value |= (long)bytes[index] << (index * 8);
        }

        return value;
    }

    private static long ReadSignedInteger(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        long value = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            value |= (long)bytes[index] << (index * 8);
        }

        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
        {
            value |= -1L << (bytes.Length * 8);
        }

        return value;
    }

    private static void ReadExactlyAt(FileStream stream, Span<byte> buffer, long offset)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    private static string ToVolumePath(string driveRoot)
    {
        var root = Path.GetPathRoot(driveRoot) ?? driveRoot;
        var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (drive.Length != 2 || drive[1] != ':')
        {
            throw new NotSupportedException("NTFS fast index only supports local drive-letter volumes.");
        }

        return $@"\\.\{drive}";
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string SafeGet(Func<string> valueFactory)
    {
        try
        {
            return valueFactory();
        }
        catch (Exception ex) when (IsExpectedFastIndexFailure(ex))
        {
            return string.Empty;
        }
    }

    private static uint? TryGetVolumeSerialNumber(string driveRoot)
    {
        var root = EnsureTrailingSeparator(driveRoot);
        return NativeMethods.GetVolumeInformation(
            root,
            null,
            0,
            out var serialNumber,
            out _,
            out _,
            null,
            0)
            ? serialNumber
            : null;
    }

    private static DateTime SafeFileTime(long fileTime)
    {
        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool IsExpectedFastIndexFailure(Exception ex)
    {
        return ex is UnauthorizedAccessException
            or IOException
            or Win32Exception
            or InvalidDataException
            or NotSupportedException
            or ArgumentException;
    }

    private sealed record NtfsBootSector(
        int BytesPerSector,
        int BytesPerCluster,
        long MftLogicalClusterNumber,
        int FileRecordSize)
    {
        public long MftByteOffset => MftLogicalClusterNumber * BytesPerCluster;
    }

    private sealed record MftDataMap(
        IReadOnlyList<NtfsDataRun> Runs,
        long DataSize,
        long InitializedSize);

    private sealed record NtfsDataRun(
        long LogicalClusterNumber,
        long ClusterCount);

    private sealed record UsnJournalData(
        ulong UsnJournalId,
        long FirstUsn,
        long NextUsn,
        long LowestValidUsn);

    private sealed record IndexedSnapshot(
        IReadOnlyList<FolderSizeEntry> Folders,
        long TotalBytes,
        int FileCount);

    private sealed class FolderSizeEntryBuilder(string relativePath, string name)
    {
        public string RelativePath { get; } = relativePath;

        public string Name { get; } = name;

        public long Size { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData(
        long startUsn,
        uint reasonMask,
        uint returnOnlyOnClose,
        ulong timeout,
        ulong bytesToWaitFor,
        ulong usnJournalId)
    {
        public long StartUsn = startUsn;
        public uint ReasonMask = reasonMask;
        public uint ReturnOnlyOnClose = returnOnlyOnClose;
        public ulong Timeout = timeout;
        public ulong BytesToWaitFor = bytesToWaitFor;
        public ulong UsnJournalId = usnJournalId;
    }

    private sealed class NtfsRecordEntry(long recordNumber, bool isDirectory)
    {
        public long RecordNumber { get; } = recordNumber;

        public bool IsDirectory { get; } = isDirectory;

        public long DataSize { get; set; }

        public long FileNameSize { get; set; }

        public long Size => Math.Max(DataSize, FileNameSize);

        public List<NtfsFileName> Names { get; } = [];

        public IEnumerable<NtfsFileName> GetVisibleNames()
        {
            foreach (var name in Names)
            {
                if (IsPreferredVisibleName(name))
                {
                    yield return name;
                }
            }

            foreach (var name in Names)
            {
                if (IsSecondaryVisibleName(name))
                {
                    yield return name;
                }
            }
        }

        public NtfsFileName? GetPreferredVisibleName()
        {
            NtfsFileName? fallback = null;
            foreach (var name in Names)
            {
                if (IsPreferredVisibleName(name))
                {
                    return name;
                }

                if (fallback is null && IsSecondaryVisibleName(name))
                {
                    fallback = name;
                }
            }

            return fallback;
        }

        private static bool IsPreferredVisibleName(NtfsFileName name)
        {
            return name.NamespaceId == 1 && name.Name is not "." and not "..";
        }

        private static bool IsSecondaryVisibleName(NtfsFileName name)
        {
            return name.NamespaceId is not 1 and not 2 && name.Name is not "." and not "..";
        }
    }

    private sealed record NtfsFileName(
        long ParentRecordNumber,
        string Name,
        byte NamespaceId,
        FileAttributes Attributes,
        DateTime LastWriteTimeUtc,
        long RealSize)
    {
        public bool IsReparsePoint => Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint FileShareDelete = 0x00000004;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x00000080;
        public const uint FileFlagSequentialScan = 0x08000000;
        public const uint FsctlQueryUsnJournal = 0x000900F4;
        public const uint FsctlReadUsnJournal = 0x000900BB;
        public const uint ReasonAll = 0xFFFFFFFF;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inBuffer,
            int inBufferSize,
            ref byte outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inBuffer,
            int inBufferSize,
            byte[] outBuffer,
            int outBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder? fileSystemNameBuffer,
            int fileSystemNameSize);
    }
}
