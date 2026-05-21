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

        Accumulate(snapshot.Files, isSnapshot: true, index, root);
        Accumulate(current.Files, isSnapshot: false, index, root);
        SortChildren(root);

        var largest = index.Values
            .Where(static node => node.RelativePath.Length > 0)
            .OrderByDescending(static node => Math.Abs(node.DeltaBytes))
            .ThenBy(static node => node.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, largestChangeCount))
            .ToArray();

        return new SnapshotComparison(snapshot, current, root, largest);
    }

    private static void Accumulate(
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
