using System.IO.Compression;
using System.Text.Json;

namespace DiskCompare.Core.Snapshots;

public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task SaveAsync(
        Snapshot snapshot,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var output = File.Create(filePath);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Snapshot> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var input = File.OpenRead(filePath);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(gzip, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return snapshot ?? throw new InvalidDataException("Snapshot file is empty or invalid.");
    }

    public string GetDefaultSnapshotDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "DiskCompare", "Snapshots");
    }

    public string CreateDefaultSnapshotPath(Snapshot snapshot)
    {
        var driveName = snapshot.DriveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(":", string.Empty)
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        var timestamp = snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        return Path.Combine(GetDefaultSnapshotDirectory(), $"{driveName}-{timestamp}.dcsnap");
    }
}
