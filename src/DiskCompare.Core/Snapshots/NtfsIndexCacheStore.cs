using DiskCompare.Core;

namespace DiskCompare.Core.Snapshots;

internal sealed class NtfsIndexCacheStore
{
    private const string CacheExtension = ".ntfsindex";
    private const string CacheMagic = "DCNTFSIDX";
    private const long MaxCacheFileBytes = 1024L * 1024 * 1024;
    private const int MaxCacheRecords = 10_000_000;
    private const int MaxNamesPerRecord = 8;
    private const int MaxCachedStringLength = 32_767;
    private const int MaxCachesPerVolume = 3;

    private readonly string _cacheDirectory;

    public NtfsIndexCacheStore(string? cacheDirectory = null)
    {
        _cacheDirectory = NormalizeDirectory(cacheDirectory ?? GetDefaultCacheDirectoryCore());
    }

    public NtfsIndexCache? TryLoad(string driveRoot, uint volumeSerialNumber)
    {
        NtfsIndexCache? bestCache = null;
        foreach (var cachePath in EnumerateCachePaths(driveRoot, volumeSerialNumber))
        {
            try
            {
                EnsureFileIsNotReparsePoint(cachePath);
                var info = new FileInfo(cachePath);
                if (info.Length > MaxCacheFileBytes)
                {
                    continue;
                }

                using var input = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 256 * 1024);
                using var reader = new BinaryReader(input);
                var cache = ReadCache(reader);
                if (cache.SchemaVersion != NtfsIndexCache.CurrentSchemaVersion
                    || !string.Equals(EnsureTrailingSeparator(cache.DriveRoot), EnsureTrailingSeparator(driveRoot), StringComparison.OrdinalIgnoreCase)
                    || cache.VolumeSerialNumber != volumeSerialNumber)
                {
                    continue;
                }

                if (bestCache is null || cache.NextUsn > bestCache.NextUsn)
                {
                    bestCache = cache;
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                continue;
            }
        }

        return bestCache;
    }

    public void Save(NtfsIndexCache cache)
    {
        EnsureCacheDirectoryExists();

        var cachePath = CreateUniqueCachePath(cache.DriveRoot, cache.VolumeSerialNumber, cache.NextUsn);
        using (var output = CreateCacheFileStream(cachePath))
        using (var writer = new BinaryWriter(output))
        {
            WriteCache(writer, cache);
        }

        EnsureFileIsNotReparsePoint(cachePath);
        PruneOldCaches(cache.DriveRoot, cache.VolumeSerialNumber);
    }

    private static NtfsIndexCache ReadCache(BinaryReader reader)
    {
        if (!string.Equals(ReadBoundedString(reader), CacheMagic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("NTFS index cache header is invalid.");
        }

        var schemaVersion = reader.ReadInt32();
        var driveRoot = ReadBoundedString(reader);
        var fileSystem = ReadBoundedString(reader);
        var volumeSerialNumber = reader.ReadUInt32();
        var usnJournalId = reader.ReadUInt64();
        var nextUsn = reader.ReadInt64();
        var lowestValidUsn = reader.ReadInt64();
        var updatedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var recordCount = reader.ReadInt32();
        if (recordCount < 0 || recordCount > MaxCacheRecords)
        {
            throw new InvalidDataException("NTFS index cache record count is invalid.");
        }

        var records = new NtfsCachedRecord[recordCount];
        for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            var recordNumber = reader.ReadInt64();
            var isDirectory = reader.ReadBoolean();
            var dataSize = reader.ReadInt64();
            var fileNameSize = reader.ReadInt64();
            var nameCount = reader.ReadInt32();
            if (nameCount < 0 || nameCount > MaxNamesPerRecord)
            {
                throw new InvalidDataException("NTFS index cache name count is invalid.");
            }

            var names = new NtfsCachedName[nameCount];
            for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
            {
                names[nameIndex] = new NtfsCachedName(
                    reader.ReadInt64(),
                    ReadBoundedString(reader),
                    reader.ReadByte(),
                    (FileAttributes)reader.ReadInt32(),
                    new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                    reader.ReadInt64());
            }

            records[recordIndex] = new NtfsCachedRecord(recordNumber, isDirectory, dataSize, fileNameSize, names);
        }

        return new NtfsIndexCache(
            schemaVersion,
            driveRoot,
            fileSystem,
            volumeSerialNumber,
            usnJournalId,
            nextUsn,
            lowestValidUsn,
            updatedAtUtc,
            records);
    }

    private static string ReadBoundedString(BinaryReader reader)
    {
        var value = reader.ReadString();
        if (value.Length > MaxCachedStringLength)
        {
            throw new InvalidDataException("NTFS index cache string is too long.");
        }

        return value;
    }

    private static void WriteCache(BinaryWriter writer, NtfsIndexCache cache)
    {
        writer.Write(CacheMagic);
        writer.Write(cache.SchemaVersion);
        writer.Write(cache.DriveRoot);
        writer.Write(cache.FileSystem);
        writer.Write(cache.VolumeSerialNumber);
        writer.Write(cache.UsnJournalId);
        writer.Write(cache.NextUsn);
        writer.Write(cache.LowestValidUsn);
        writer.Write(cache.UpdatedAtUtc.Ticks);
        writer.Write(cache.Records.Length);

        foreach (var record in cache.Records)
        {
            writer.Write(record.RecordNumber);
            writer.Write(record.IsDirectory);
            writer.Write(record.DataSize);
            writer.Write(record.FileNameSize);
            writer.Write(record.Names.Length);

            foreach (var name in record.Names)
            {
                writer.Write(name.ParentRecordNumber);
                writer.Write(name.Name);
                writer.Write(name.NamespaceId);
                writer.Write((int)name.Attributes);
                writer.Write(name.LastWriteTimeUtc.Ticks);
                writer.Write(name.RealSize);
            }
        }
    }

    private void EnsureCacheDirectoryExists()
    {
        EnsureNoExistingAncestorIsReparsePoint(_cacheDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        EnsureDirectoryIsNotReparsePoint(_cacheDirectory);
    }

    private IEnumerable<string> EnumerateCachePaths(string driveRoot, uint volumeSerialNumber)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return [];
        }

        EnsureDirectoryIsNotReparsePoint(_cacheDirectory);
        var prefix = GetCacheFilePrefix(driveRoot, volumeSerialNumber);
        return Directory.EnumerateFiles(_cacheDirectory, $"{prefix}-*{CacheExtension}");
    }

    private void PruneOldCaches(string driveRoot, uint volumeSerialNumber)
    {
        var cachePaths = EnumerateCachePaths(driveRoot, volumeSerialNumber)
            .Select(path => new FileInfo(path))
            .Where(static info => !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(static info => info.CreationTimeUtc)
            .ThenByDescending(static info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(MaxCachesPerVolume)
            .ToArray();

        foreach (var cache in cachePaths)
        {
            var fullPath = Path.GetFullPath(cache.FullName);
            var directory = NormalizeDirectory(Path.GetDirectoryName(fullPath));
            if (!string.Equals(directory, _cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsureFileIsNotReparsePoint(fullPath);
            File.Delete(fullPath);
        }
    }

    private string CreateUniqueCachePath(string driveRoot, uint volumeSerialNumber, long nextUsn)
    {
        var prefix = GetCacheFilePrefix(driveRoot, volumeSerialNumber);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:000}";
            var fileName = $"{prefix}-{nextUsn:X16}-{Guid.NewGuid():N}{suffix}{CacheExtension}";
            var fullPath = Path.GetFullPath(Path.Combine(_cacheDirectory, fileName));
            var directory = NormalizeDirectory(Path.GetDirectoryName(fullPath));
            if (!string.Equals(directory, _cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("NTFS index cache path escaped the DiskCompare cache directory.");
            }

            if (!File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new IOException("Could not create a unique NTFS index cache path.");
    }

    private FileStream CreateCacheFileStream(string safePath)
    {
        EnsureDirectoryIsNotReparsePoint(_cacheDirectory);
        var directory = NormalizeDirectory(Path.GetDirectoryName(safePath));
        if (!string.Equals(directory, _cacheDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("NTFS index cache path escaped the DiskCompare cache directory.");
        }

        return new FileStream(
            safePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 256 * 1024);
    }

    private static string GetCacheFilePrefix(string driveRoot, uint volumeSerialNumber)
    {
        var root = Path.GetPathRoot(driveRoot) ?? driveRoot;
        var driveName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(":", string.Empty)
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');

        return $"{driveName}-{volumeSerialNumber:X8}";
    }

    private static void EnsureNoExistingAncestorIsReparsePoint(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Refusing to write NTFS index cache through a reparse point: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Refusing to use a reparse point as the NTFS index cache directory: {info.FullName}");
        }
    }

    private static void EnsureFileIsNotReparsePoint(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Refusing to use a reparse point as the NTFS index cache file: {filePath}");
        }
    }

    private static string GetDefaultCacheDirectoryCore()
    {
        return DiskCompareDataPaths.GetIndexCacheDirectory();
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("NTFS index cache directory is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static bool IsRecoverable(Exception ex)
    {
        return ex is UnauthorizedAccessException
            or IOException
            or InvalidDataException
            or NotSupportedException
            or ArgumentException;
    }
}
