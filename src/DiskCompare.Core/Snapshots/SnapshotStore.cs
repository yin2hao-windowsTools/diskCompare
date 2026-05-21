using System.IO.Compression;
using System.Text.Json;

namespace DiskCompare.Core.Snapshots;

public sealed class SnapshotStore
{
    public const string SnapshotExtension = ".dcsnap";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _snapshotDirectory;

    public SnapshotStore(string? snapshotDirectory = null)
    {
        _snapshotDirectory = NormalizeDirectory(snapshotDirectory ?? GetDefaultSnapshotDirectoryCore());
    }

    public async Task SaveAsync(
        Snapshot snapshot,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var safePath = ValidateSnapshotOutputPath(filePath);
        EnsureSnapshotDirectoryExists();

        await using var output = new FileStream(
            safePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Snapshot> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var safePath = ValidateSnapshotInputPath(filePath);
        await using var input = File.OpenRead(safePath);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(gzip, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return snapshot ?? throw new InvalidDataException("Snapshot file is empty or invalid.");
    }

    public void EnsureSnapshotDirectoryExists()
    {
        EnsureNoExistingAncestorIsReparsePoint(_snapshotDirectory);
        Directory.CreateDirectory(_snapshotDirectory);
        EnsureDirectoryIsNotReparsePoint(_snapshotDirectory);
    }

    public string GetDefaultSnapshotDirectory()
    {
        return _snapshotDirectory;
    }

    public string CreateDefaultSnapshotPath(Snapshot snapshot)
    {
        var driveName = snapshot.DriveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(":", string.Empty)
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        var timestamp = snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        return CreateUniquePath($"{driveName}-{timestamp}");
    }

    private string CreateUniquePath(string baseFileName)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt:000}";
            var candidate = Path.Combine(_snapshotDirectory, $"{baseFileName}{suffix}{SnapshotExtension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not create a unique snapshot path.");
    }

    private string ValidateSnapshotOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Snapshot output path is required.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.Equals(NormalizeDirectory(directory), _snapshotDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Snapshot files can only be written to the DiskCompare snapshot directory.");
        }

        if (!string.Equals(Path.GetExtension(fullPath), SnapshotExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Snapshot files must use the {SnapshotExtension} extension.");
        }

        if (File.Exists(fullPath))
        {
            throw new IOException("Refusing to overwrite an existing snapshot file.");
        }

        return fullPath;
    }

    private static void EnsureNoExistingAncestorIsReparsePoint(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Refusing to write snapshots through a reparse point: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Refusing to use a reparse point as the snapshot directory: {info.FullName}");
        }
    }

    private static string ValidateSnapshotInputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Snapshot input path is required.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Snapshot file does not exist.", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), SnapshotExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Snapshot files must use the {SnapshotExtension} extension.");
        }

        return fullPath;
    }

    private static string GetDefaultSnapshotDirectoryCore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "DiskCompare", "Snapshots");
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Snapshot directory is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
