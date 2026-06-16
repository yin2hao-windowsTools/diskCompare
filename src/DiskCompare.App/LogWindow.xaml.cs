using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Windows;

namespace DiskCompare.App;

public partial class LogWindow : Window
{
    private readonly ObservableCollection<ScanLogEntryViewModel> _entries;

    public LogWindow(ObservableCollection<ScanLogEntryViewModel> entries)
    {
        InitializeComponent();
        _entries = entries;
        LogDataGrid.ItemsSource = _entries;
        _entries.CollectionChanged += Entries_CollectionChanged;
        UpdateSummary();
    }

    protected override void OnClosed(EventArgs e)
    {
        _entries.CollectionChanged -= Entries_CollectionChanged;
        base.OnClosed(e);
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSummary();
        if (_entries.Count > 0)
        {
            LogDataGrid.ScrollIntoView(_entries[^1]);
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        Clipboard.SetText(BuildLogText(_entries));
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (LogDataGrid.SelectedItem is ScanLogEntryViewModel entry)
        {
            Clipboard.SetText(entry.DetailText);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateSummary()
    {
        var message = _entries.Count == 0
            ? "当前扫描没有记录到错误。"
            : $"已记录 {_entries.Count:N0} 条扫描错误，最近一条: {_entries[^1].Path}";
        SummaryTextBlock.Text = message;
        EmptyStateTextBlock.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildLogText(IEnumerable<ScanLogEntryViewModel> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(entry.DetailText);
        }

        return builder.ToString();
    }
}
