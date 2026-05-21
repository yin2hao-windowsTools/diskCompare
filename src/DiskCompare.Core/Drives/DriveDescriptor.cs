namespace DiskCompare.Core.Drives;

public sealed record DriveDescriptor(
    string Name,
    string RootPath,
    string Format,
    string Type,
    bool IsReady,
    long TotalSize,
    long AvailableFreeSpace);
