using System.Drawing;
using System.Drawing.Text;

namespace ShotTrans;

/// <summary>仅用于 UI 自动化的确定性英文测试窗口，不参与生产流程。</summary>
internal sealed class TestPageForm : Form
{
    public TestPageForm()
    {
        Text = "ShotTrans E2E Test Page";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(0, 0, 1800, 1000);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(246, 247, 249);
        TopMost = false;
        KeyPreview = true;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var sample = new TextBox
        {
            Left = 40,
            Top = 570,
            Width = 1700,
            Height = 42,
            Text = "Click GetProcAddress to load kernel32.dll. Version 10.0.19041.",
            Font = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel),
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            TabStop = false,
            ShortcutsEnabled = true,
            HideSelection = false
        };
        Controls.Add(sample);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using var title = new Font("Arial", 56, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Arial", 32, FontStyle.Regular, GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", 30, FontStyle.Regular, GraphicsUnit.Pixel);
        using var black = new SolidBrush(Color.FromArgb(18, 22, 28));
        using var gray = new SolidBrush(Color.FromArgb(88, 100, 114));
        using var white = new SolidBrush(Color.FromArgb(244, 246, 250));
        using var dark = new SolidBrush(Color.FromArgb(24, 28, 35));

        g.DrawString("System Settings", title, black, 40, 30);
        g.DrawString("Control who and which devices are allowed to connect in your network.", body, black, 40, 120);
        g.DrawString("Click GetProcAddress to load kernel32.dll. Version 10.0.19041.", body, black, 40, 165);
        g.DrawString("The download failed: Access denied.", body, black, 40, 210);
        g.DrawString("Open C:\\Windows\\System32\\cmd.exe and try again.", body, black, 40, 255);

        using (var path = Rounded(new RectangleF(40, 310, 1700, 80), 8))
            g.FillPath(dark, path);
        g.DrawString("Dark background: Access denied. Check the hosts file.", mono, white, 60, 333);

        g.DrawString("This page is a deterministic test target for the screenshot overlay.", body, gray, 40, 420);
        g.DrawString("Extra line: Space to compare, Ctrl+C to copy, R to retry.", body, gray, 40, 465);
        g.DrawString("Select the text in the box below to test Ctrl+Alt+Z selection translation.", body, gray, 40, 510);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
