using System.Drawing;
using System.Windows.Forms;

namespace DiskCompare.Launcher;

internal sealed class RuntimeDownloadPrompt : Form
{
    public RuntimeDownloadPrompt()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        ClientSize = new Size(520, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "DiskCompare";

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var headerPanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 16)
        };
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var iconBox = new PictureBox
        {
            Image = SystemIcons.Warning.ToBitmap(),
            Margin = new Padding(0, 4, 16, 0),
            Size = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.StretchImage
        };

        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 14F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(24, 24, 27),
            Margin = new Padding(0),
            MaximumSize = new Size(420, 0),
            Text = "需要先安装 .NET 8 Desktop Runtime"
        };

        headerPanel.Controls.Add(iconBox, 0, 0);
        headerPanel.Controls.Add(titleLabel, 1, 0);

        var descriptionLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.75F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(63, 63, 70),
            Margin = new Padding(0, 0, 0, 12),
            MaximumSize = new Size(460, 0),
            Text = RuntimeRequirement.GetMissingRuntimeMessage()
        };

        var linkCaptionLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(39, 39, 42),
            Margin = new Padding(0, 0, 0, 6),
            MaximumSize = new Size(460, 0),
            Text = RuntimeRequirement.DownloadLinkCaption
        };

        var downloadLinkLabel = new LinkLabel
        {
            ActiveLinkColor = Color.FromArgb(37, 99, 235),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            LinkColor = Color.FromArgb(37, 99, 235),
            Margin = new Padding(0, 0, 0, 12),
            MaximumSize = new Size(460, 0),
            Text = RuntimeRequirement.DownloadPageUrl,
            VisitedLinkColor = Color.FromArgb(29, 78, 216)
        };
        downloadLinkLabel.LinkClicked += OnOpenDownloadPage;

        var footnoteLabel = new Label
        {
            AutoSize = true,
            BackColor = Color.FromArgb(244, 244, 245),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(63, 63, 70),
            Margin = new Padding(0),
            MaximumSize = new Size(460, 0),
            Padding = new Padding(12, 10, 12, 10),
            Text = RuntimeRequirement.GetMissingRuntimeFootnote()
        };

        var buttonPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 20, 0, 0),
            WrapContents = false
        };

        var closeButton = new Button
        {
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(12, 0, 0, 0),
            Padding = new Padding(10, 0, 10, 0),
            Text = "关闭"
        };
        closeButton.Click += (_, _) => Close();

        var openButton = new Button
        {
            AutoSize = true,
            BackColor = Color.FromArgb(37, 99, 235),
            FlatAppearance = { BorderSize = 0 },
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            Margin = new Padding(0),
            Padding = new Padding(12, 0, 12, 0),
            Text = RuntimeRequirement.DownloadButtonText,
            UseVisualStyleBackColor = false
        };
        openButton.Click += OnOpenDownloadPage;

        AcceptButton = openButton;
        CancelButton = closeButton;

        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Controls.Add(openButton);

        layout.Controls.Add(headerPanel, 0, 0);
        layout.Controls.Add(descriptionLabel, 0, 1);
        layout.Controls.Add(linkCaptionLabel, 0, 2);
        layout.Controls.Add(downloadLinkLabel, 0, 3);
        layout.Controls.Add(footnoteLabel, 0, 4);
        layout.Controls.Add(buttonPanel, 0, 5);
        layout.RowCount = 6;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(layout);
    }

    private void OnOpenDownloadPage(object? sender, EventArgs e)
    {
        try
        {
            RuntimeRequirement.OpenDownloadPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"打开 .NET 官网失败：{ex.Message}",
                "DiskCompare",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
