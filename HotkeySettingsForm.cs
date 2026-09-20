namespace RouterSpeed;

public sealed class HotkeySettingsForm : Form
{
    private readonly Func<ShortcutDefinition?, string?> _apply;
    private readonly CheckBox _enabled = new() { Text = "启用全局快捷键", AutoSize = true };
    private readonly CheckBox _control = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox _alt = new() { Text = "Alt", AutoSize = true };
    private readonly CheckBox _shift = new() { Text = "Shift", AutoSize = true };
    private readonly CheckBox _win = new() { Text = "Win", AutoSize = true };
    private readonly ComboBox _key = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _preview = new();
    private readonly Label _error = new();

    public ShortcutDefinition? SavedShortcut { get; private set; }

    public HotkeySettingsForm(ShortcutDefinition? current, Func<ShortcutDefinition?, string?> apply)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Text = "RouterSpeed · 显示 / 隐藏快捷键";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(496, 352);
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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(layout);

        var description = new Label
        {
            Text = "在其他程序中也能快速显示或隐藏网速条。", Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(178, 193, 211)
        };
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 2);
        layout.Controls.Add(_enabled, 0, 1);
        layout.SetColumnSpan(_enabled, 2);
        layout.Controls.Add(FieldLabel("组合键"), 0, 2);
        var modifiers = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 0) };
        foreach (var box in new[] { _control, _alt, _shift, _win })
        {
            box.Margin = new Padding(0, 0, 16, 0);
            box.CheckedChanged += (_, _) => UpdatePreview();
            modifiers.Controls.Add(box);
        }
        layout.Controls.Add(modifiers, 1, 2);
        layout.Controls.Add(FieldLabel("主按键"), 0, 3);
        _key.Dock = DockStyle.Top;
        _key.Margin = new Padding(0, 4, 0, 0);
        _key.BackColor = Color.FromArgb(38, 46, 61);
        _key.ForeColor = ForeColor;
        _key.DisplayMember = nameof(KeyChoice.Label);
        _key.Items.AddRange(ShortcutDefinition.AvailableKeys.Select(key => new KeyChoice(key, ShortcutDefinition.KeyText(key))).ToArray());
        _key.SelectedIndexChanged += (_, _) => UpdatePreview();
        layout.Controls.Add(_key, 1, 3);
        _preview.Dock = DockStyle.Fill;
        _preview.Padding = new Padding(0, 6, 0, 0);
        _preview.ForeColor = Color.FromArgb(124, 213, 218);
        layout.Controls.Add(_preview, 0, 4);
        layout.SetColumnSpan(_preview, 2);
        _error.Dock = DockStyle.Fill;
        _error.ForeColor = Color.FromArgb(238, 187, 113);
        layout.Controls.Add(_error, 0, 5);
        layout.SetColumnSpan(_error, 2);

        var restore = MakeButton("恢复默认", (_, _) => SetSelection(ShortcutDefinition.Default));
        layout.Controls.Add(restore, 0, 7);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var save = MakeButton("保存", (_, _) => SaveShortcut());
        var cancel = MakeButton("取消", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 1, 7);
        AcceptButton = save;
        CancelButton = cancel;
        _enabled.CheckedChanged += (_, _) => UpdatePreview();
        SetSelection(current);
    }

    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private static Button MakeButton(string text, EventHandler clicked)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(74, 31), Height = 31,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(43, 55, 74), ForeColor = Color.FromArgb(238, 243, 251),
            Margin = new Padding(0, 0, 7, 0)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(71, 88, 111);
        button.Click += clicked;
        return button;
    }

    private void SetSelection(ShortcutDefinition? shortcut)
    {
        ShortcutDefinition display = shortcut ?? ShortcutDefinition.Default;
        _enabled.Checked = shortcut is not null;
        _control.Checked = display.Control;
        _alt.Checked = display.Alt;
        _shift.Checked = display.Shift;
        _win.Checked = display.Win;
        Keys selectedKey = ShortcutDefinition.AvailableKeys.Contains(display.Key) ? display.Key : ShortcutDefinition.Default.Key;
        _key.SelectedItem = _key.Items.Cast<KeyChoice>().First(choice => choice.Key == selectedKey);
        UpdatePreview();
    }

    private ShortcutDefinition? Selection => !_enabled.Checked ? null : new(
        _key.SelectedItem is KeyChoice choice ? choice.Key : Keys.None,
        _control.Checked, _alt.Checked, _shift.Checked, _win.Checked);

    private void UpdatePreview()
    {
        foreach (var control in new Control[] { _control, _alt, _shift, _win, _key })
            control.Enabled = _enabled.Checked;
        _preview.Text = Selection is { } shortcut ? "显示 / 隐藏：" + shortcut.DisplayText : "快捷键已停用；仍可通过托盘图标显示网速条。";
        _error.Text = "";
    }

    private void SaveShortcut()
    {
        var selected = Selection;
        if (selected?.Validate() is { } invalid) { _error.Text = invalid; return; }
        try
        {
            if (_apply(selected) is { } error) { _error.Text = error; return; }
            SavedShortcut = selected;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            _error.Text = "暂时无法应用快捷键，请稍后重试。";
        }
    }

    private sealed record KeyChoice(Keys Key, string Label);
}
