using System.Collections.Concurrent;

namespace DiskCompare.Core.Snapshots;

public sealed class SnapshotBuilder
{
    private const int ProgressInterval = 250;

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

        return await Task.Run(() => CreateCore(normalizedRoot, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static Snapshot CreateCore(
        string driveRoot,
        IProgress<SnapshotProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = EnsureTrailingSeparator(driveRoot);
        var files = new List<FileEntry>(capacity: 64 * 1024);
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
                    files.Add(new FileEntry(NormalizeRelativePath(relativePath), info.Length, info.LastWriteTimeUtc));
                    filesScanned++;
                    bytesScanned += info.Length;

                    if (filesScanned % ProgressInterval == 0)
                    {
                        progress?.Report(new SnapshotProgress(info.FullName, filesScanned, bytesScanned, errors.Count));
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    errors.Enqueue(new ScanError(filePath, ex.Message));
                }
            }
        }

        progress?.Report(new SnapshotProgress(root, filesScanned, bytesScanned, errors.Count));

        var drive = new DriveInfo(root);
        return new Snapshot(
            root,
            SafeGet(() => drive.VolumeLabel),
            SafeGet(() => drive.DriveFormat),
            DateTime.UtcNow,
            files.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            errors.ToArray());
    }

    private static IEnumerable<string> EnumerateDirectories(string path, ConcurrentQueue<ScanError> errors)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
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
            return Directory.EnumerateFiles(path);
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
}
