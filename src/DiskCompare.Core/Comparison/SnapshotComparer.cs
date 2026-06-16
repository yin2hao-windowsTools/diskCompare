using DiskCompare.Core.Snapshots;

namespace DiskCompare.Core.Comparison;

public sealed class SnapshotComparer
{
    public SnapshotComparison Compare(Snapshot snapshot, Snapshot current, int largestChangeCount = 100)
    {
        var rootName = string.IsNullOrWhiteSpace(snapshot.DriveRoot) ? current.DriveRoot : snapshot.DriveRoot;
        var root = new FolderDelta(rootName, string.Empty);
        var index = new Dictionary<string, FolderDelta>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root
        };

        AccumulateSnapshot(snapshot, isSnapshot: true, index, root);
        AccumulateSnapshot(current, isSnapshot: false, index, root);

        SortChildren(root);

        var largest = index.Values
            .Where(static node => node.RelativePath.Length > 0)
            .OrderByDescending(static node => Math.Abs(node.DeltaBytes))
            .ThenBy(static node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, largestChangeCount))
            .ToArray();

        return new SnapshotComparison(snapshot, current, root, largest);
    }

    private static void AccumulateSnapshot(
        Snapshot snapshot,
        bool isSnapshot,
        Dictionary<string, FolderDelta> index,
        FolderDelta root)
    {
        if (snapshot.FolderSizes.Count > 0 || snapshot.TotalBytesOverride is not null)
        {
            AccumulateFolders(snapshot.FolderSizes, isSnapshot, index, root);
            AddSize(root, snapshot.TotalBytes, isSnapshot);
            return;
        }

        AccumulateFiles(snapshot.Files, isSnapshot, index, root);
    }

    private static void AccumulateFolders(
        IReadOnlyList<FolderSizeEntry> folders,
        bool isSnapshot,
        Dictionary<string, FolderDelta> index,
        FolderDelta root)
    {
        foreach (var folder in folders)
        {
            var path = folder.RelativePath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var current = EnsureFolderPath(path, folder.Name, index, root);
            AddSize(current, folder.Size, isSnapshot);
        }
    }

    private static FolderDelta EnsureFolderPath(
        string relativePath,
        string name,
        Dictionary<string, FolderDelta> index,
        FolderDelta root)
    {
        if (index.TryGetValue(relativePath, out var existing))
        {
            return existing;
        }

        var parentPath = GetParentPath(relativePath);
        var parent = string.IsNullOrEmpty(parentPath)
            ? root
            : EnsureFolderPath(parentPath, GetName(parentPath), index, root);
        var child = new FolderDelta(name, relativePath);
        index[relativePath] = child;
        parent.Children.Add(child);
        return child;
    }

    private static void AccumulateFiles(
        IReadOnlyList<FileEntry> files,
        bool isSnapshot,
        Dictionary<string, FolderDelta> index,
        FolderDelta root)
    {
        foreach (var file in files)
        {
            AddSize(root, file.Size, isSnapshot);
            var relativePath = file.RelativePath;
            var path = string.Empty;
            var current = root;
            var segmentStart = 0;

            for (var indexInPath = 0; indexInPath < relativePath.Length; indexInPath++)
            {
                if (!IsDirectorySeparator(relativePath[indexInPath]))
                {
                    continue;
                }

                if (indexInPath == segmentStart)
                {
                    segmentStart++;
                    continue;
                }

                var segment = relativePath[segmentStart..indexInPath];
                path = string.IsNullOrEmpty(path)
                    ? segment
                    : string.Concat(path, Path.DirectorySeparatorChar, segment);

                if (!index.TryGetValue(path, out var child))
                {
                    child = new FolderDelta(segment, path);
                    index[path] = child;
                    current.Children.Add(child);
                }

                AddSize(child, file.Size, isSnapshot);
                current = child;
                segmentStart = indexInPath + 1;
            }
        }
    }

    private static string GetParentPath(string relativePath)
    {
        var separator = relativePath.LastIndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private static string GetName(string relativePath)
    {
        var separator = relativePath.LastIndexOf(Path.DirectorySeparatorChar);
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    private static void AddSize(FolderDelta node, long size, bool isSnapshot)
    {
        if (isSnapshot)
        {
            node.SnapshotBytes += size;
        }
        else
        {
            node.CurrentBytes += size;
        }
    }

    private static void SortChildren(FolderDelta node)
    {
        node.Children.Sort(static (left, right) =>
        {
            var deltaCompare = Math.Abs(right.DeltaBytes).CompareTo(Math.Abs(left.DeltaBytes));
            return deltaCompare != 0
                ? deltaCompare
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children)
        {
            SortChildren(child);
        }
    }
}
