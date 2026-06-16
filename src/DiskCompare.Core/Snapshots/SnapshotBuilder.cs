using System.Collections.Concurrent;

namespace DiskCompare.Core.Snapshots;

public sealed class SnapshotBuilder
{
    private const int ProgressInterval = 250;
    private readonly NtfsMftSnapshotProvider _ntfsMftSnapshotProvider = new();

    public async Task<Snapshot> CreateAsync(
        string driveRoot,
        IProgress<SnapshotProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new ArgumentException("Drive root is required.", nameof(driveRoot));
        }

        var normalizedRoot = Path.GetFullPath(driveRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(normalizedRoot);
        }

        return await Task.Run(() => CreateFastestAvailable(normalizedRoot, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private Snapshot CreateFastestAvailable(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastSnapshot = _ntfsMftSnapshotProvider.TryCreate(
            driveRoot,
            progress,
            cancellationToken,
            out var fallbackReason);

        if (fastSnapshot is not null)
        {
            return fastSnapshot;
        }

        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            progress?.Report(new SnapshotProgress($"NTFS 快速索引不可用，回退目录扫描: {fallbackReason}", 0, 0, 0, "目录扫描"));
        }

        return CreateCore(driveRoot, progress, cancellationToken);
    }

    private static Snapshot CreateCore(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = EnsureTrailingSeparator(driveRoot);
        var folderSizes = new Dictionary<string, FolderSizeEntryBuilder>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentQueue<ScanError>();
        var stack = new Stack<string>();
        stack.Push(root);

        var filesScanned = 0;
        long bytesScanned = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var directory in EnumerateDirectories(current, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stack.Push(directory);
            }

            foreach (var filePath in EnumerateFiles(current, errors))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(filePath);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(root, info.FullName);
                    var normalizedRelativePath = NormalizeRelativePath(relativePath);
                    AddFolderSizes(folderSizes, normalizedRelativePath, info.Length);
                    filesScanned++;
                    bytesScanned += info.Length;

                    if (filesScanned % ProgressInterval == 0)
                    {
                        progress?.Report(new SnapshotProgress(info.FullName, filesScanned, bytesScanned, errors.Count, "目录扫描"));
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    errors.Enqueue(new ScanError(filePath, ex.Message));
                }
            }
        }

        progress?.Report(new SnapshotProgress(root, filesScanned, bytesScanned, errors.Count, "目录扫描"));

        var drive = new DriveInfo(root);
        return new Snapshot(
            root,
            SafeGet(() => drive.VolumeLabel),
            SafeGet(() => drive.DriveFormat),
            DateTime.UtcNow,
            [],
            errors.ToArray(),
            folderSizes.Values
                .Select(static folder => new FolderSizeEntry(folder.RelativePath, folder.Name, folder.Size))
                .ToArray(),
            bytesScanned,
            filesScanned);
    }

    private static IEnumerable<string> EnumerateDirectories(string path, ConcurrentQueue<ScanError> errors)
    {
        try
        {
            var directories = new List<string>();
            foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
            {
                try
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        continue;
                    }

                    directories.Add(directory.FullName);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    errors.Enqueue(new ScanError(directory.FullName, ex.Message));
                }
            }

            return directories;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            errors.Enqueue(new ScanError(path, ex.Message));
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path, ConcurrentQueue<ScanError> errors)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles()
                .Select(static file => file.FullName)
                .ToArray();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            errors.Enqueue(new ScanError(path, ex.Message));
            return Array.Empty<string>();
        }
    }

    private static string SafeGet(Func<string> valueFactory)
    {
        try
        {
            return valueFactory();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return string.Empty;
        }
    }

    private static bool IsRecoverable(Exception ex)
    {
        return ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or PathTooLongException
            or NotSupportedException;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static void AddFolderSizes(Dictionary<string, FolderSizeEntryBuilder> folderSizes, string fileRelativePath, long size)
    {
        var lastSeparator = fileRelativePath.LastIndexOf(Path.DirectorySeparatorChar);
        if (lastSeparator <= 0)
        {
            return;
        }

        for (var separatorIndex = lastSeparator; separatorIndex > 0;)
        {
            var path = fileRelativePath[..separatorIndex];
            AddFolderSize(folderSizes, path, size);
            var previousSeparator = path.LastIndexOf(Path.DirectorySeparatorChar);
            if (previousSeparator < 0)
            {
                break;
            }

            separatorIndex = previousSeparator;
        }
    }

    private static void AddFolderSize(Dictionary<string, FolderSizeEntryBuilder> folderSizes, string relativePath, long size)
    {
        if (!folderSizes.TryGetValue(relativePath, out var folder))
        {
            folder = new FolderSizeEntryBuilder(relativePath, GetName(relativePath));
            folderSizes[relativePath] = folder;
        }

        folder.Size += size;
    }

    private static string GetName(string relativePath)
    {
        var separator = relativePath.LastIndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    private sealed class FolderSizeEntryBuilder(string relativePath, string name)
    {
        public string RelativePath { get; } = relativePath;

        public string Name { get; } = name;

        public long Size { get; set; }
    }
}
