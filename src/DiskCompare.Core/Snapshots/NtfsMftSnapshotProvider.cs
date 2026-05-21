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
        progress?.Report(new SnapshotProgress("正在读取 NTFS MFT 元数据...", 0, 0, 0, "NTFS MFT 快速索引"));

        var mftMap = ReadMftDataMap(stream, boot);
        var recordCount = mftMap.InitializedSize / boot.FileRecordSize;
        if (recordCount <= FirstUserRecordNumber)
        {
            throw new InvalidDataException("The NTFS MFT does not contain user records.");
        }

        var entries = ReadRecords(stream, boot, mftMap, recordCount, progress, cancellationToken);
        var files = BuildFileEntries(entries);

        progress?.Report(new SnapshotProgress("NTFS MFT 快速索引完成", files.Count, files.Sum(static file => file.Size), 0, "NTFS MFT 快速索引"));

        return new Snapshot(
            EnsureTrailingSeparator(driveRoot),
            SafeGet(() => drive.VolumeLabel),
            format,
            DateTime.UtcNow,
            files,
            []);
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

    private static List<FileEntry> BuildFileEntries(Dictionary<long, NtfsRecordEntry> entries)
    {
        var files = new List<FileEntry>(entries.Count);
        var directoryPathCache = new Dictionary<long, string?> { [RootDirectoryRecordNumber] = string.Empty };

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

                var parentPath = ResolveDirectoryPath(name.ParentRecordNumber, entries, directoryPathCache, []);
                if (parentPath is null)
                {
                    continue;
                }

                var relativePath = string.IsNullOrEmpty(parentPath)
                    ? name.Name
                    : Path.Combine(parentPath, name.Name);

                files.Add(new FileEntry(relativePath, entry.Size, name.LastWriteTimeUtc));
            }
        }

        return files;
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

        var name = entry.GetVisibleNames().FirstOrDefault();
        if (name is null || name.Name == ".")
        {
            cache[recordNumber] = string.Empty;
            visiting.Remove(recordNumber);
            return string.Empty;
        }

        var parentPath = ResolveDirectoryPath(name.ParentRecordNumber, entries, cache, visiting);
        var path = parentPath is null
            ? null
            : string.IsNullOrEmpty(parentPath) ? name.Name : Path.Combine(parentPath, name.Name);

        cache[recordNumber] = path;
        visiting.Remove(recordNumber);
        return path;
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
            return Names
                .Where(static name => name.NamespaceId != 2 && name.Name is not "." and not "..")
                .OrderBy(static name => name.NamespaceId == 1 ? 0 : 1)
                .ThenBy(static name => name.Name, StringComparer.OrdinalIgnoreCase);
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

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);
    }
}
