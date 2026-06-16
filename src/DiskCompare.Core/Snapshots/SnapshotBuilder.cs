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

        return CreateDirectorySnapshot(driveRoot, progress, cancellationToken);
    }

    internal static Snapshot CreateDirectorySnapshot(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = EnsureTrailingSeparator(driveRoot);
        var folderSizes = new Dictionary<string, FolderSizeEntryBuilder>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<ScanError>();
        var stack = new Stack<(string FullPath, string RelativePath)>();
        stack.Push((root, string.Empty));

        var filesScanned = 0;
        long bytesScanned = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            try
            {
                foreach (var item in new DirectoryInfo(current.FullPath).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if ((item.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            continue;
                        }

                        if ((item.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            stack.Push((item.FullName, GetChildRelativePath(current.RelativePath, item.Name)));
                            continue;
                        }

                        if (item is not FileInfo file)
                        {
                            continue;
                        }

                        AddFolderSizes(folderSizes, current.RelativePath, file.Length);
                        filesScanned++;
                        bytesScanned += file.Length;

                        if (filesScanned % ProgressInterval == 0)
                        {
                            progress?.Report(new SnapshotProgress(file.FullName, filesScanned, bytesScanned, errors.Count, "目录扫描"));
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        AddScanError(errors, item.FullName, ex, filesScanned, bytesScanned, progress);
                    }
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                AddScanError(errors, current.FullPath, ex, filesScanned, bytesScanned, progress);
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
            ToFolderEntries(folderSizes),
            bytesScanned,
            filesScanned);
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

    private static void AddScanError(
        List<ScanError> errors,
        string path,
        Exception exception,
        int filesScanned,
        long bytesScanned,
        IProgress<SnapshotProgress>? progress)
    {
        var error = new ScanError(path, exception.Message);
        errors.Add(error);
        progress?.Report(new SnapshotProgress(path, filesScanned, bytesScanned, errors.Count, "目录扫描", error));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(fullPath)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string GetChildRelativePath(string parentRelativePath, string childName)
    {
        return string.IsNullOrEmpty(parentRelativePath)
            ? childName
            : string.Concat(parentRelativePath, Path.DirectorySeparatorChar, childName);
    }

    private static void AddFolderSizes(Dictionary<string, FolderSizeEntryBuilder> folderSizes, string parentRelativePath, long size)
    {
        if (string.IsNullOrEmpty(parentRelativePath))
        {
            return;
        }

        for (var separatorIndex = parentRelativePath.Length; separatorIndex > 0;)
        {
            var path = parentRelativePath[..separatorIndex];
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

    private sealed class FolderSizeEntryBuilder(string relativePath, string name)
    {
        public string RelativePath { get; } = relativePath;

        public string Name { get; } = name;

        public long Size { get; set; }
    }
}
