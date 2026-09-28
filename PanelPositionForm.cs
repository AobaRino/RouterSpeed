namespace RouterSpeed;

internal sealed class PanelPositionForm : Form
{
    private readonly NumericUpDown _x = new() { Minimum = -100000, Maximum = 100000, Dock = DockStyle.Fill };
    private readonly NumericUpDown _y = new() { Minimum = -100000, Maximum = 100000, Dock = DockStyle.Fill };
    internal Point PanelLocation => new((int)_x.Value, (int)_y.Value);

    internal PanelPositionForm(Point location)
    {
        Text = "RouterSpeed · 位置设置";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(390, 205);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        _x.Value = Math.Clamp(location.X, -100000, 100000);
        _y.Value = Math.Clamp(location.Y, -100000, 100000);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var description = new Label { Text = "设置浮窗左上角的屏幕坐标。多屏坐标可以为负数，保存时会限制在可见区域内。", Dock = DockStyle.Fill };
        layout.Controls.Add(description, 0, 0);
        layout.SetColumnSpan(description, 2);
        layout.Controls.Add(new Label { Text = "水平位置 X", AutoSize = true }, 0, 1);
        layout.Controls.Add(_x, 1, 1);
        layout.Controls.Add(new Label { Text = "垂直位置 Y", AutoSize = true }, 0, 2);
        layout.Controls.Add(_y, 1, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
