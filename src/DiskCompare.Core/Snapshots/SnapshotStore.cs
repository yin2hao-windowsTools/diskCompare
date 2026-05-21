using System.IO.Compression;
using System.Text.Json;

namespace DiskCompare.Core.Snapshots;

public sealed class SnapshotStore
{
    public const string SnapshotExtension = ".dcsnap";
    private const long MaxCompressedSnapshotBytes = 256L * 1024 * 1024;
    private const long MaxDecompressedSnapshotBytes = 1024L * 1024 * 1024;
    private const int MaxSnapshotFileEntries = 5_000_000;
    private const int MaxSnapshotFolderEntries = 2_000_000;
    private const int MaxSnapshotErrors = 250_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16
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

        await using (var output = CreateSnapshotFileStream(safePath))
        {
            await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
            await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureFileIsNotReparsePoint(safePath);
    }

    public async Task<Snapshot> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var safePath = ValidateSnapshotInputPath(filePath);
        var info = new FileInfo(safePath);
        if (info.Length > MaxCompressedSnapshotBytes)
        {
            throw new InvalidDataException($"Snapshot file is too large. Maximum allowed compressed size is {MaxCompressedSnapshotBytes:N0} bytes.");
        }

        await using var input = new FileStream(
            safePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var limited = new LimitedReadStream(gzip, MaxDecompressedSnapshotBytes);
        var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(limited, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return ValidateLoadedSnapshot(snapshot);
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

    private FileStream CreateSnapshotFileStream(string safePath)
    {
        EnsureDirectoryIsNotReparsePoint(_snapshotDirectory);
        var directory = Path.GetDirectoryName(safePath);
        if (!string.Equals(NormalizeDirectory(directory), _snapshotDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Snapshot files can only be written to the DiskCompare snapshot directory.");
        }

        return new FileStream(
            safePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
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

    private static void EnsureFileIsNotReparsePoint(string filePath)
    {
        var attributes = File.GetAttributes(filePath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"Refusing to use a reparse point as the snapshot file: {filePath}");
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

    private static Snapshot ValidateLoadedSnapshot(Snapshot? snapshot)
    {
        if (snapshot is null)
        {
            throw new InvalidDataException("Snapshot file is empty or invalid.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.DriveRoot))
        {
            throw new InvalidDataException("Snapshot drive root is missing.");
        }

        if (snapshot.Files.Count > MaxSnapshotFileEntries)
        {
            throw new InvalidDataException($"Snapshot contains too many file entries. Maximum allowed count is {MaxSnapshotFileEntries:N0}.");
        }

        if (snapshot.FolderSizes.Count > MaxSnapshotFolderEntries)
        {
            throw new InvalidDataException($"Snapshot contains too many folder entries. Maximum allowed count is {MaxSnapshotFolderEntries:N0}.");
        }

        if (snapshot.Errors.Count > MaxSnapshotErrors)
        {
            throw new InvalidDataException($"Snapshot contains too many scan errors. Maximum allowed count is {MaxSnapshotErrors:N0}.");
        }

        foreach (var file in snapshot.Files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath) || Path.IsPathRooted(file.RelativePath) || file.Size < 0)
            {
                throw new InvalidDataException("Snapshot contains an invalid file entry.");
            }
        }

        foreach (var folder in snapshot.FolderSizes)
        {
            if (string.IsNullOrWhiteSpace(folder.RelativePath) || string.IsNullOrWhiteSpace(folder.Name) || Path.IsPathRooted(folder.RelativePath) || folder.Size < 0)
            {
                throw new InvalidDataException("Snapshot contains an invalid folder entry.");
            }
        }

        return snapshot;
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

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            AddBytes(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            AddBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            AddBytes(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private void AddBytes(int read)
        {
            _bytesRead += read;
            if (_bytesRead > maxBytes)
            {
                throw new InvalidDataException($"Snapshot expands beyond the allowed limit of {maxBytes:N0} bytes.");
            }
        }
    }
}
