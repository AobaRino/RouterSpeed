using System.Text.Json;

namespace RouterSpeed;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupTrace.Write(args.Contains("--startup") ? "login-start" : "manual-start");
        try { return Run(args); }
        catch (Exception ex)
        {
            StartupTrace.Write("startup-failed", ex);
            if (!args.Contains("--startup") && !args.Contains("--diagnose") && !args.Contains("--set-startup"))
                MessageBox.Show("网速条启动失败，错误类型已记录到本地启动日志。请重新打开程序。", "RouterSpeed");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        ApplicationConfiguration.Initialize();
        int startupAt = Array.IndexOf(args, "--set-startup");
        if (startupAt >= 0)
        {
            if (startupAt + 1 >= args.Length || args[startupAt + 1] is not ("on" or "off")) return 2;
            bool enabled = args[startupAt + 1] == "on";
            bool saved = new StartupRegistration().TrySetEnabled(enabled, out _);
            StartupTrace.Write(saved ? enabled ? "startup-enabled" : "startup-disabled" : "startup-registration-failed");
            return saved ? 0 : 2;
        }
        int importAt = Array.IndexOf(args, "--import");
        if (importAt >= 0)
        {
            if (importAt + 1 >= args.Length) return 2;
            try { RouterSettings.FromExportFile(args[importAt + 1]).Save(); }
            catch { return 2; }
            // Import is deliberately noninteractive and never deletes the user's source file.
            if (!args.Contains("--diagnose")) return 0;
        }
        RouterSettings settings;
        try { settings = RouterSettings.Load(); }
        catch
        {
            if (args.Contains("--diagnose")) return 2;
            settings = new();
            MessageBox.Show("连接设置无法读取，请重新导入路由器提供的连接信息。", "RouterSpeed");
        }
        if (args.Contains("--diagnose"))
        {
            using var diagnosticConnection = new RouterConnection(settings);
            var samples = new List<SpeedSnapshot>();
            for (int i = 0; i < 12; i++)
            {
                samples.Add(diagnosticConnection.PollAsync(CancellationToken.None).GetAwaiter().GetResult());
                if (i < 11) Thread.Sleep(1000);
            }
            File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "diagnostics.json"),
                JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        using var singleton = new Mutex(true, "Local\\RouterSpeed-192.168.233.1", out bool first);
        if (!first)
        {
            StartupTrace.Write("existing-instance");
            return 0;
        }
        if (string.IsNullOrEmpty(settings.ProtectedToken))
        {
            using var setup = new ConnectionSettingsForm(settings) { StartPosition = FormStartPosition.CenterScreen };
            if (setup.ShowDialog() == DialogResult.OK && setup.SavedSettings is { } initial) settings = initial;
        }
        RouterConnection connection = new(settings);
        SpeedBarForm? bar = null;
        bool connectedLogged = false;
        try
        {
            bar = new SpeedBarForm(async cancellationToken =>
            {
                RouterConnection active = connection;
                SpeedSnapshot snapshot = await active.PollAsync(cancellationToken);
                if (ReferenceEquals(active, connection) && snapshot.Connected && !connectedLogged)
                {
                    connectedLogged = true;
                    StartupTrace.Write("data-connected");
                }
                return ReferenceEquals(active, connection) ? snapshot : new(0, 0, 0, 0,
                    "正在更新连接", "连接设置已保存，正在读取新的统计数据。", false);
            }, () =>
            {
                using var dialog = new ConnectionSettingsForm(settings);
                if (dialog.ShowDialog(bar) != DialogResult.OK || dialog.SavedSettings is not { } updated) return;
                var previous = connection;
                settings = updated;
                connection = new RouterConnection(settings);
                previous.Dispose();
            });
            bar.Shown += (_, _) => StartupTrace.Write("window-shown");
            Application.Run(bar);
            StartupTrace.Write("normal-exit");
        }
        finally { bar?.Dispose(); connection.Dispose(); }
        return 0;
    }
}
