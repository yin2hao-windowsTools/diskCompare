namespace DiskCompare.Core.Drives;

public sealed class DriveService
{
    public IReadOnlyList<DriveDescriptor> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(static drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .OrderBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static drive =>
            {
                if (!drive.IsReady)
                {
                    return new DriveDescriptor(
                        drive.Name,
                        drive.RootDirectory.FullName,
                        string.Empty,
                        drive.DriveType.ToString(),
                        false,
                        0,
                        0);
                }

                return new DriveDescriptor(
                    drive.Name,
                    drive.RootDirectory.FullName,
                    drive.DriveFormat,
                    drive.DriveType.ToString(),
                    true,
                    drive.TotalSize,
                    drive.AvailableFreeSpace);
            })
            .ToArray();
    }
}
