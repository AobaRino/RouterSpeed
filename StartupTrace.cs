using System.Globalization;

namespace RouterSpeed;

internal static class StartupTrace
{
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouterSpeed", "startup.log");

    internal static void Write(string stage, Exception? exception = null)
    {
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 65536)
                File.Move(path, path + ".previous", overwrite: true);
            // Never log arguments, exception messages, network data, or credentials.
            string failure = exception is null ? "" : $" {exception.GetType().Name} 0x{exception.HResult:X8}";
            File.AppendAllText(path, $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} pid={Environment.ProcessId} {stage}{failure}{Environment.NewLine}");
        }
        catch { /* Logging must never prevent the speed bar from starting. */ }
    }
}
