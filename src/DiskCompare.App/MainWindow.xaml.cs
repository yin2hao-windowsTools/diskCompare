using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskCompare.Core.Comparison;
using DiskCompare.Core.Drives;
using DiskCompare.Core.Snapshots;
using Microsoft.Win32;

namespace DiskCompare.App;

public partial class MainWindow : Window
{
    private readonly DriveService _driveService = new();
    private readonly SnapshotBuilder _snapshotBuilder = new();
    private readonly SnapshotStore _snapshotStore = new();
    private readonly SnapshotComparer _snapshotComparer = new();
    private readonly ReleaseUpdateService _releaseUpdateService = new();
    private readonly ApplicationUpdater _applicationUpdater = new();
    private readonly ObservableCollection<DriveViewModel> _drives = [];
    private readonly ObservableCollection<FolderDeltaViewModel> _treeNodes = [];
    private readonly ObservableCollection<FolderDeltaViewModel> _largestChanges = [];

    private Snapshot? _loadedSnapshot;
    private CancellationTokenSource? _scanCancellation;
    private bool _updateInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DriveComboBox.ItemsSource = _drives;
        DeltaTreeView.DataContext = _treeNodes;
        LargestChangesListView.DataContext = _largestChanges;
        RefreshDrives();
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrives();
    }

    private void DriveComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is DriveViewModel drive)
        {
            var snapshotHint = _loadedSnapshot is not null
                && !_loadedSnapshot.DriveRoot.Equals(drive.RootPath, StringComparison.OrdinalIgnoreCase)
                    ? $"\n已加载快照属于 {_loadedSnapshot.DriveRoot}，对比时会扫描该盘当前状态。"
                    : string.Empty;
            DriveInfoTextBlock.Text = drive.Description + snapshotHint;
            CompareButton.IsEnabled = _loadedSnapshot is not null && _scanCancellation is null;
        }
    }

    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var drive = GetSelectedDrive();
        if (drive is null)
        {
            return;
        }

        await RunScanAsync(
            $"正在创建 {drive.RootPath} 快照...",
            async token =>
            {
                var snapshot = await CreateSnapshotAsync(drive.RootPath, token);
                var filePath = _snapshotStore.CreateDefaultSnapshotPath(snapshot);
                await _snapshotStore.SaveAsync(snapshot, filePath, token);
                _loadedSnapshot = snapshot;
                UpdateSnapshotInfo(filePath);
                ClearComparison();
                StatusTextBlock.Text = $"快照已保存: {filePath}";
            });
    }

    private async void LoadSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "加载磁盘快照",
            Filter = "DiskCompare 快照 (*.dcsnap)|*.dcsnap|所有文件 (*.*)|*.*",
            InitialDirectory = _snapshotStore.GetDefaultSnapshotDirectory()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _loadedSnapshot = await _snapshotStore.LoadAsync(dialog.FileName);
            UpdateSnapshotInfo(dialog.FileName);
            ClearComparison();
            SelectDrive(_loadedSnapshot.DriveRoot);
            StatusTextBlock.Text = $"已加载快照: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ShowError("无法加载快照", ex);
        }
    }

    private async void CompareSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedSnapshot is null)
        {
            MessageBox.Show(this, "请先创建或加载一个快照。", "缺少快照", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var driveRoot = _loadedSnapshot.DriveRoot;
        if (!Directory.Exists(driveRoot))
        {
            MessageBox.Show(
                this,
                $"快照所属盘 {_loadedSnapshot.DriveRoot} 当前不可用。为避免误扫其他磁盘，请重新接入该盘或加载对应盘符的快照。",
                "快照盘符不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await RunScanAsync(
            $"正在扫描 {driveRoot} 当前状态...",
            async token =>
            {
                var current = await CreateSnapshotAsync(driveRoot, token);
                var comparison = _snapshotComparer.Compare(_loadedSnapshot, current, largestChangeCount: 200);
                ShowComparison(comparison);
                StatusTextBlock.Text = "对比完成";
            });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        StatusTextBlock.Text = "正在取消...";
    }

    private void OpenSnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = _snapshotStore.GetDefaultSnapshotDirectory();
        _snapshotStore.EnsureSnapshotDirectoryExists();
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void OpenDeveloperHome_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUri(ReleaseUpdateService.DeveloperHomeUrl);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null)
        {
            MessageBox.Show(this, "扫描进行中，完成或取消扫描后再更新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_updateInProgress)
        {
            MessageBox.Show(this, "更新检查正在进行中。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _updateInProgress = true;
        StatusTextBlock.Text = "正在检查更新...";

        try
        {
            var latestRelease = await _releaseUpdateService.GetLatestReleaseAsync();
            var currentDisplayVersion = ReleaseUpdateService.GetCurrentDisplayVersion();
            var currentVersion = ReleaseUpdateService.GetCurrentVersion();

            if (latestRelease is null)
            {
                StatusTextBlock.Text = "暂无可用 Release";
                MessageBox.Show(this, "GitHub 仓库当前没有可用 Release。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (latestRelease.Version is not null && currentVersion is not null)
            {
                if (latestRelease.Version > currentVersion)
                {
                    StatusTextBlock.Text = $"发现新版本 {latestRelease.TagName}";
                    await PromptInstallUpdateAsync(latestRelease, currentDisplayVersion, title: "发现新版本");
                    return;
                }

                StatusTextBlock.Text = "当前已是最新版本";
                MessageBox.Show(
                    this,
                    $"当前已是最新版本。\n当前版本: {currentDisplayVersion}\nGitHub 最新版本: {latestRelease.Name} ({latestRelease.TagName})",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            StatusTextBlock.Text = "已获取最新 Release";
            await PromptInstallUpdateAsync(latestRelease, currentDisplayVersion, title: "已找到最新 Release");
        }
        catch (Exception ex)
        {
            ShowError("检查更新失败", ex);
        }
        finally
        {
            _updateInProgress = false;
            SetBusy(false);
            ActivityProgressBar.Value = 0;
        }
    }

    private void ShowLicense_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "许可证: 当前应用未内置 LICENSE 文件。\n\n请访问 GitHub 仓库确认最新许可证说明，或联系开发者获取授权信息。\n\n是否打开 GitHub 仓库？",
            "许可证",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            OpenExternalUri(ReleaseUpdateService.RepositoryUrl);
        }
    }

    private void ShowAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            $"DiskCompare\n版本: {ReleaseUpdateService.GetCurrentDisplayVersion()}\n\n磁盘快照与文件夹体积变化对比工具。\n\n开发者主页: {ReleaseUpdateService.DeveloperHomeUrl}\nGitHub 仓库: {ReleaseUpdateService.RepositoryUrl}",
            "关于 DiskCompare",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task PromptInstallUpdateAsync(ReleaseInfo latestRelease, string currentDisplayVersion, string title)
    {
        var asset = _applicationUpdater.SelectPreferredAsset(latestRelease);
        if (asset is null)
        {
            PromptOpenRelease(
                title,
                $"当前版本: {currentDisplayVersion}\nGitHub Release: {latestRelease.Name} ({latestRelease.TagName})\n\n未找到可自动安装的 exe 或 msi 文件，是否打开 Release 页面？",
                latestRelease.HtmlUrl);
            return;
        }

        var packageKind = ApplicationUpdater.GetPackageKind(asset);
        var actionText = packageKind == UpdatePackageKind.MsiInstaller
            ? "下载完成后会启动安装程序并退出 DiskCompare。"
            : "下载完成后会退出 DiskCompare，覆盖当前程序文件并重启。";
        var result = MessageBox.Show(
            this,
            $"当前版本: {currentDisplayVersion}\n最新版本: {latestRelease.Name} ({latestRelease.TagName})\n\n{actionText}\n\n更新文件: {asset.Name}\n是否下载并自动更新？",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            await DownloadAndApplyUpdateAsync(asset);
        }
    }

    private async Task DownloadAndApplyUpdateAsync(ReleaseAsset asset)
    {
        SetBusy(true);
        ActivityProgressBar.Value = 0;
        ActivityProgressBar.IsIndeterminate = asset.Size <= 0;
        StatusTextBlock.Text = "正在下载更新...";
        ProgressTextBlock.Text = $"正在下载 {asset.Name}";

        var progress = new Progress<UpdateDownloadProgress>(value => ShowUpdateDownloadProgress(asset, value));
        var package = await _applicationUpdater.DownloadAsync(asset, progress);

        StatusTextBlock.Text = "更新已下载";
        ProgressTextBlock.Text = $"更新已下载: {Path.GetFileName(package.FilePath)}";

        var packageKind = package.Kind == UpdatePackageKind.MsiInstaller ? "启动安装程序" : "覆盖当前程序并重启";
        var result = MessageBox.Show(
            this,
            $"更新文件已下载完成。\n\n下一步将{packageKind}，当前程序会退出。\n是否继续？",
            "准备安装更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes)
        {
            StatusTextBlock.Text = "更新已下载，尚未安装";
            return;
        }

        StatusTextBlock.Text = "正在启动更新...";
        _applicationUpdater.ApplyUpdateAndRestart(package);
        Application.Current.Shutdown();
    }

    private void ShowUpdateDownloadProgress(ReleaseAsset asset, UpdateDownloadProgress progress)
    {
        var totalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : asset.Size;
        if (totalBytes <= 0)
        {
            ActivityProgressBar.IsIndeterminate = true;
            StatusTextBlock.Text = "正在下载更新...";
            ProgressTextBlock.Text = $"正在下载 {asset.Name}\n{FormatBytes(progress.ReceivedBytes)}";
            return;
        }

        var percentage = Math.Clamp(progress.ReceivedBytes * 100.0 / totalBytes, 0, 100);
        ActivityProgressBar.IsIndeterminate = false;
        ActivityProgressBar.Value = percentage;
        StatusTextBlock.Text = $"正在下载更新... {percentage:N0}%";
        ProgressTextBlock.Text = $"正在下载 {asset.Name}\n{FormatBytes(progress.ReceivedBytes)} / {FormatBytes(totalBytes)}";
    }

    private void RefreshDrives()
    {
        _drives.Clear();
        foreach (var drive in _driveService.GetDrives().Select(static drive => new DriveViewModel(drive)))
        {
            _drives.Add(drive);
        }

        DriveComboBox.SelectedIndex = _drives.Count > 0 ? 0 : -1;
        StatusTextBlock.Text = _drives.Count == 0 ? "未发现可用磁盘" : "就绪";
    }

    private DriveViewModel? GetSelectedDrive()
    {
        if (DriveComboBox.SelectedItem is DriveViewModel drive)
        {
            return drive;
        }

        MessageBox.Show(this, "请先选择一个盘符。", "缺少盘符", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void SelectDrive(string rootPath)
    {
        var drive = _drives.FirstOrDefault(item => item.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase));
        if (drive is not null)
        {
            DriveComboBox.SelectedItem = drive;
        }
    }

    private async Task<Snapshot> CreateSnapshotAsync(string driveRoot, CancellationToken cancellationToken)
    {
        var progress = new Progress<SnapshotProgress>(value =>
        {
            var mode = string.IsNullOrWhiteSpace(value.Mode) ? "扫描" : value.Mode;
            ProgressTextBlock.Text = $"{mode}\n{FormatBytes(value.BytesScanned)} / {value.FilesScanned:N0} 个文件 / {value.ErrorCount:N0} 个错误\n{value.CurrentPath}";
        });

        return await _snapshotBuilder.CreateAsync(driveRoot, progress, cancellationToken);
    }

    private async Task RunScanAsync(string startingStatus, Func<CancellationToken, Task> action)
    {
        if (_scanCancellation is not null)
        {
            return;
        }

        _scanCancellation = new CancellationTokenSource();
        SetBusy(true);
        StatusTextBlock.Text = startingStatus;
        ProgressTextBlock.Text = startingStatus;

        try
        {
            await action(_scanCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "已取消";
            ProgressTextBlock.Text = "扫描已取消";
        }
        catch (Exception ex)
        {
            ShowError("扫描失败", ex);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ActivityProgressBar.IsIndeterminate = busy;
        CreateSnapshotButton.IsEnabled = !busy;
        LoadSnapshotButton.IsEnabled = !busy;
        CompareButton.IsEnabled = !busy && _loadedSnapshot is not null;
        CancelButton.IsEnabled = busy && _scanCancellation is not null;
        DriveComboBox.IsEnabled = !busy;
    }

    private void UpdateSnapshotInfo(string? path)
    {
        if (_loadedSnapshot is null)
        {
            SnapshotInfoTextBlock.Text = "尚未加载快照";
            CompareButton.IsEnabled = false;
            return;
        }

        SnapshotInfoTextBlock.Text =
            $"{_loadedSnapshot.DriveRoot}  {_loadedSnapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            $"{_loadedSnapshot.FileCount:N0} 个文件 / {FormatBytes(_loadedSnapshot.TotalBytes)} / {_loadedSnapshot.Errors.Count:N0} 个错误\n" +
            $"{path ?? string.Empty}";
        CompareButton.IsEnabled = _scanCancellation is null;
    }

    private void ShowComparison(SnapshotComparison comparison)
    {
        SnapshotBytesTextBlock.Text = FormatBytes(comparison.Snapshot.TotalBytes);
        CurrentBytesTextBlock.Text = FormatBytes(comparison.Current.TotalBytes);
        DeltaBytesTextBlock.Text = FormatSignedBytes(comparison.DeltaBytes);
        DeltaBytesTextBlock.Foreground = GetDeltaBrush(comparison.DeltaBytes);
        FileAndErrorTextBlock.Text = $"{comparison.Current.FileCount:N0} / {comparison.Current.Errors.Count:N0}";

        var maxDelta = Math.Max(1, comparison.LargestChanges.Select(static item => Math.Abs(item.DeltaBytes)).DefaultIfEmpty(1).Max());
        _treeNodes.Clear();
        _treeNodes.Add(new FolderDeltaViewModel(comparison.Root, maxDelta, isRoot: true));

        _largestChanges.Clear();
        foreach (var change in comparison.LargestChanges.Where(static item => item.DeltaBytes != 0).Take(100))
        {
            _largestChanges.Add(new FolderDeltaViewModel(change, maxDelta, isRoot: false));
        }

        TreeHintTextBlock.Text = "快照 / 当前 / 变化";
    }

    private void ClearComparison()
    {
        SnapshotBytesTextBlock.Text = "-";
        CurrentBytesTextBlock.Text = "-";
        DeltaBytesTextBlock.Text = "-";
        DeltaBytesTextBlock.Foreground = Brushes.Black;
        FileAndErrorTextBlock.Text = "-";
        TreeHintTextBlock.Text = string.Empty;
        _treeNodes.Clear();
        _largestChanges.Clear();
    }

    private void ShowError(string title, Exception ex)
    {
        StatusTextBlock.Text = title;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void PromptOpenRelease(string title, string message, Uri releaseUrl)
    {
        var result = MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            OpenExternalUri(releaseUrl.ToString());
        }
    }

    private void OpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError("无法打开链接", ex);
        }
    }

    internal static string FormatSignedBytes(long value)
    {
        if (value == 0)
        {
            return "0 B";
        }

        return value > 0 ? $"+{FormatBytes(value)}" : $"-{FormatBytes(Math.Abs(value))}";
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = Math.Abs((double)bytes);
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        return unitIndex == 0
            ? $"{sign}{value:N0} {units[unitIndex]}"
            : $"{sign}{value:N1} {units[unitIndex]}";
    }

    internal static Brush GetDeltaBrush(long delta)
    {
        if (delta > 0)
        {
            return new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }

        if (delta < 0)
        {
            return new SolidColorBrush(Color.FromRgb(22, 163, 74));
        }

        return new SolidColorBrush(Color.FromRgb(100, 116, 139));
    }
}

public sealed class DriveViewModel
{
    public DriveViewModel(DriveDescriptor drive)
    {
        RootPath = drive.RootPath;
        DisplayName = drive.IsReady
            ? $"{drive.Name}  {drive.Format}  可用 {MainWindow.FormatBytes(drive.AvailableFreeSpace)}"
            : $"{drive.Name}  未就绪";
        Description = drive.IsReady
            ? $"{drive.Type} / {drive.Format}\n总容量 {MainWindow.FormatBytes(drive.TotalSize)}，可用 {MainWindow.FormatBytes(drive.AvailableFreeSpace)}"
            : $"{drive.Type} / 未就绪";
    }

    public string RootPath { get; }

    public string DisplayName { get; }

    public string Description { get; }
}

public sealed class FolderDeltaViewModel
{
    public FolderDeltaViewModel(FolderDelta delta, long maxDelta, bool isRoot)
    {
        Name = isRoot ? delta.Name : delta.Name;
        Path = string.IsNullOrEmpty(delta.RelativePath) ? delta.Name : delta.RelativePath;
        SnapshotBytes = delta.SnapshotBytes;
        CurrentBytes = delta.CurrentBytes;
        DeltaBytes = delta.DeltaBytes;
        Kind = delta.Kind;
        Ratio = maxDelta <= 0 ? 0 : Math.Abs(delta.DeltaBytes) / (double)maxDelta;
        Children = new ObservableCollection<FolderDeltaViewModel>(
            delta.Children.Select(child => new FolderDeltaViewModel(child, maxDelta, isRoot: false)));
    }

    public string Name { get; }

    public string Path { get; }

    public long SnapshotBytes { get; }

    public long CurrentBytes { get; }

    public long DeltaBytes { get; }

    public SizeDeltaKind Kind { get; }

    public double Ratio { get; }

    public string SnapshotBytesText => MainWindow.FormatBytes(SnapshotBytes);

    public string CurrentBytesText => MainWindow.FormatBytes(CurrentBytes);

    public ObservableCollection<FolderDeltaViewModel> Children { get; }
}

public sealed class DeltaBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return MainWindow.GetDeltaBrush(value is long delta ? delta : 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class DeltaTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return MainWindow.FormatSignedBytes(value is long delta ? delta : 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class KindTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            SizeDeltaKind.Added => "新增",
            SizeDeltaKind.Removed => "删除",
            SizeDeltaKind.Increased => "增大",
            SizeDeltaKind.Decreased => "减小",
            _ => "未变化"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class RatioWidthConverter : IValueConverter
{
    private const double MaxWidth = 110;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ratio = value is double number ? number : 0;
        return Math.Clamp(ratio, 0, 1) * MaxWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
