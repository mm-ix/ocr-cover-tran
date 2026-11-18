using System.Drawing;
using ShotTrans.Ui;

namespace ShotTrans.History;

internal sealed class HistoryForm : Form
{
    private readonly HistoryStore _store;
    private readonly AppConfig _config;

    private readonly ListView _list;
    private readonly ImageList _images;
    private readonly PictureBox _picture;
    private readonly TextBox _original;
    private readonly TextBox _translated;
    private readonly Label _hint;
    private readonly Button _copyOriginal;
    private readonly Button _copyTranslated;
    private readonly Button _delete;
    private readonly Button _clear;
    private readonly Button _close;

    private List<HistoryEntry> _entries = new();

    public HistoryForm(HistoryStore store, AppConfig config)
    {
        _store = store;
        _config = config;

        Text = "ShotTrans 历史记录";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 640);
        MinimumSize = new Size(820, 520);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = AppIcon.CreateTrayIcon();
        ShowInTaskbar = true;

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            ForeColor = Color.FromArgb(170, 90, 60),
            Text = "历史记录当前已关闭（在设置中开启后才会记录新条目）"
        };

        _images = new ImageList { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth32Bit };
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            SmallImageList = _images,
            BorderStyle = BorderStyle.None
        };
        _list.Columns.Add("时间", 125);
        _list.Columns.Add("原文", 190);
        _list.Columns.Add("译文", 190);
        _list.SelectedIndexChanged += (_, _) => LoadSelected();

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        _picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(245, 246, 248),
            BorderStyle = BorderStyle.FixedSingle
        };
        right.Controls.Add(_picture, 0, 0);

        _original = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White
        };
        right.Controls.Add(WrapWithLabel("原文", _original), 0, 1);

        _translated = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White
        };
        right.Controls.Add(WrapWithLabel("译文", _translated), 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _copyOriginal = new Button { Text = "复制原文", Width = 92, Height = 30 };
        _copyTranslated = new Button { Text = "复制译文", Width = 92, Height = 30 };
        _delete = new Button { Text = "删除", Width = 82, Height = 30 };
        buttons.Controls.AddRange(new Control[] { _copyOriginal, _copyTranslated, _delete });
        right.Controls.Add(buttons, 0, 3);

        _copyOriginal.Click += (_, _) => CopyToClipboard(_original.Text);
        _copyTranslated.Click += (_, _) => CopyToClipboard(_translated.Text);
        _delete.Click += (_, _) => DeleteSelected();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 520,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        _clear = new Button { Text = "清空全部", Width = 100, Height = 30, Left = 8, Top = 7 };
        _close = new Button { Text = "关闭", Width = 92, Height = 30, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        _close.Left = bottom.Width - _close.Width - 12;
        _close.Top = 7;
        _close.Left = 0;
        bottom.Controls.Add(_clear);
        bottom.Controls.Add(_close);
        bottom.Resize += (_, _) => _close.Left = bottom.Width - _close.Width - 12;
        _close.Click += (_, _) => Close();
        _clear.Click += (_, _) => ClearAll();

        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(_hint);
        if (_config.HistoryEnabled) _hint.Visible = false;

        Shown += (_, _) =>
        {
            _close.Left = bottom.Width - _close.Width - 12;
            LoadEntries();
        };
        FormClosed += (_, _) => _images.Dispose();
    }

    private static Control WrapWithLabel(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var label = new Label { Text = title, Dock = DockStyle.Top, Height = 22, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
        content.Dock = DockStyle.Fill;
        panel.Controls.Add(content);
        panel.Controls.Add(label);
        return panel;
    }

    private void LoadEntries()
    {
        _entries = _store.List();
        _images.Images.Clear();
        _list.Items.Clear();

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int imageIndex = -1;
            if (e.Thumbnail is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(e.Thumbnail);
                    using var img = Image.FromStream(ms);
                    _images.Images.Add((Image)img.Clone());
                    imageIndex = _images.Images.Count - 1;
                }
                catch { }
            }

            var item = new ListViewItem(e.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(OneLine(e.Original, 80));
            item.SubItems.Add(OneLine(e.Translated, 100));
            item.ImageIndex = imageIndex;
            item.Tag = e;
            _list.Items.Add(item);
        }

        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
            _list.Select();
        }
        else
        {
            _picture.Image = null;
            _original.Text = "";
            _translated.Text = "";
        }
    }

    private static string OneLine(string text, int max)
    {
        var s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private void LoadSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not HistoryEntry e) return;

        _original.Text = e.Original;
        _translated.Text = e.Translated;
        _picture.Image?.Dispose();
        _picture.Image = null;
        if (e.Thumbnail is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(e.Thumbnail);
                using var img = Image.FromStream(ms);
                _picture.Image = (Image)img.Clone();
            }
            catch { }
        }
    }

    private void DeleteSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not HistoryEntry e) return;
        if (MessageBox.Show(this, "删除这条历史记录？", "ShotTrans", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _store.Delete(e.Id);
        LoadEntries();
    }

    private void ClearAll()
    {
        if (_entries.Count == 0) return;
        if (MessageBox.Show(this, "清空全部历史记录？此操作不可恢复。", "ShotTrans", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;
        _store.Clear();
        LoadEntries();
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Util.ClipboardService.TrySetText(text, 800)) return;
        MessageBox.Show(this, "复制失败：剪贴板被其他程序占用", "ShotTrans", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
