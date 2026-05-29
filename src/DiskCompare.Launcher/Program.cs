using System.Diagnostics;
using System.Windows.Forms;

namespace DiskCompare.Launcher;

internal static class Program
{
    private const string AppHostFileName = "DiskCompare.AppHost.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var applicationDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var appHostPath = Path.Combine(applicationDirectory, AppHostFileName);
        if (!File.Exists(appHostPath))
        {
            MessageBox.Show(
                $"未找到主程序文件：{AppHostFileName}\r\n请重新安装 DiskCompare。",
                "DiskCompare",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        if (!RuntimeRequirement.IsSatisfied())
        {
            var result = MessageBox.Show(
                RuntimeRequirement.GetMissingRuntimeMessage(),
                "DiskCompare",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = RuntimeRequirement.DownloadPageUrl,
                    UseShellExecute = true
                });
            }

            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appHostPath,
                Arguments = BuildCommandLineArguments(args),
                UseShellExecute = true,
                WorkingDirectory = applicationDirectory
            };

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动 DiskCompare 失败：{ex.Message}",
                "DiskCompare",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string BuildCommandLineArguments(IEnumerable<string> args)
    {
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(char.IsWhiteSpace) || value.Contains('"');
        if (!needsQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
