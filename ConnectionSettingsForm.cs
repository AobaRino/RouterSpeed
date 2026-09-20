using System.Security.Cryptography;

namespace RouterSpeed;

public sealed class ConnectionSettingsForm : Form
{
    private readonly TextBox _url = new();
    private readonly TextBox _client = new();
    private readonly TextBox _token = new() { UseSystemPasswordChar = true };
    private readonly Label _notice = new();
    public RouterSettings? SavedSettings { get; private set; }

    public ConnectionSettingsForm(RouterSettings settings)
    {
        Text = "RouterSpeed · 连接设置";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(584, 412);
        MinimumSize = new Size(600, 451);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(24, 29, 40);
        ForeColor = Color.FromArgb(235, 241, 250);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        Controls.Add(layout);

        var introduction = new Label
        {
            Text = "在路由器“网速统计”页面复制或导出连接信息，然后在这里粘贴或导入。",
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(178, 193, 211)
        };
        layout.Controls.Add(introduction, 0, 0);
        layout.SetColumnSpan(introduction, 2);
        var importButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var paste = MakeButton("粘贴连接信息", (_, _) => PasteConnection());
        var import = MakeButton("导入 JSON 文件…", (_, _) => ImportConnection());
        importButtons.Controls.Add(paste);
        importButtons.Controls.Add(import);
        layout.Controls.Add(importButtons, 0, 1);
        layout.SetColumnSpan(importButtons, 2);
        AddField(layout, "接口地址", _url, 2);
        AddField(layout, "本机 IPv4", _client, 3);
        AddField(layout, "连接凭证", _token, 4);
        var storageNote = new Label
        {
            Text = "凭证由当前 Windows 用户加密保存。保存后立即生效。",
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(155, 173, 194), Padding = new Padding(0, 5, 0, 0)
        };
        layout.Controls.Add(storageNote, 0, 5);
        layout.SetColumnSpan(storageNote, 2);
        _notice.Dock = DockStyle.Fill;
        _notice.ForeColor = Color.FromArgb(238, 187, 113);
        layout.Controls.Add(_notice, 0, 6);
        layout.SetColumnSpan(_notice, 2);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var save = MakeButton("保存并连接", (_, _) => SaveConnection());
        var cancel = MakeButton("取消", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        bottom.Controls.Add(save);
        bottom.Controls.Add(cancel);
        layout.Controls.Add(bottom, 0, 7);
        layout.SetColumnSpan(bottom, 2);
        AcceptButton = save;
        CancelButton = cancel;
        Fill(settings);
    }

    private void AddField(TableLayoutPanel layout, string caption, TextBox textBox, int row)
    {
        layout.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        textBox.Dock = DockStyle.Top;
        textBox.Margin = new Padding(0, 7, 0, 0);
        textBox.BackColor = Color.FromArgb(38, 46, 61);
        textBox.ForeColor = ForeColor;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.MaxLength = textBox == _token ? 512 : 2048;
        layout.Controls.Add(textBox, 1, row);
    }

    private static Button MakeButton(string text, EventHandler clicked)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Height = 31, MinimumSize = new Size(94, 31),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(43, 55, 74),
            ForeColor = Color.FromArgb(238, 243, 251), Margin = new Padding(0, 0, 9, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(71, 88, 111);
        button.Click += clicked;
        return button;
    }

    private void Fill(RouterSettings settings)
    {
        _url.Text = settings.RouterUrl;
        _client.Text = settings.Client;
        try { _token.Text = settings.GetToken(); }
        catch { _token.Clear(); _notice.Text = "此前保存的凭证无法读取，请重新粘贴或导入。"; }
    }

    private void PasteConnection()
    {
        try
        {
            if (!Clipboard.ContainsText()) { _notice.Text = "剪贴板中没有连接信息。"; return; }
            Fill(RouterSettings.FromExportJson(Clipboard.GetText()));
            _notice.Text = "已读取连接信息，点击“保存并连接”应用。";
        }
        catch (FormatException ex) { _notice.Text = ex.Message; }
        catch { _notice.Text = "无法读取剪贴板，请重新复制连接信息或导入 JSON 文件。"; }
    }

    private void ImportConnection()
    {
        using var picker = new OpenFileDialog { Title = "导入路由器连接信息", Filter = "连接信息 JSON (*.json)|*.json|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Fill(RouterSettings.FromExportFile(picker.FileName));
            _notice.Text = "已读取连接信息，点击“保存并连接”应用。";
        }
        catch (FormatException ex) { _notice.Text = ex.Message; }
        catch { _notice.Text = "无法读取连接信息文件，请从路由器页面重新导出。"; }
    }

    private void SaveConnection()
    {
        try
        {
            var settings = new RouterSettings { RouterUrl = _url.Text.Trim(), Client = _client.Text.Trim() };
            settings.Validate(requireToken: false);
            settings.SetToken(_token.Text);
            settings.Save();
            SavedSettings = settings;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FormatException ex) { _notice.Text = ex.Message; }
        catch (CryptographicException) { _notice.Text = "当前 Windows 用户无法加密凭证，请重新登录后重试。"; }
        catch { _notice.Text = "无法保存连接设置，请检查程序文件夹的写入权限。"; }
    }
}
