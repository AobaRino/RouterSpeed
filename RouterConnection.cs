using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RouterSpeed;

public sealed class RouterSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string RouterUrl { get; set; } = "http://192.168.233.1/cgi-bin/router-speed";
    public string Client { get; set; } = "192.168.233.10";
    public string ProtectedToken { get; set; } = "";
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static RouterSettings Load(string? path = null)
    {
        string file = path ?? Path;
        if (!File.Exists(file)) return new();
        if (new FileInfo(file).Length > 65536) throw new FormatException("连接设置文件过大。");
        return JsonSerializer.Deserialize<RouterSettings>(File.ReadAllText(file), JsonOptions) ?? new();
    }

    public void Save(string? path = null)
    {
        Validate();
        string file = System.IO.Path.GetFullPath(path ?? Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }

    public string GetToken() => string.IsNullOrEmpty(ProtectedToken) ? "" : TokenProtection.Unprotect(ProtectedToken);
    public void SetToken(string token)
    {
        token = token.Trim();
        ValidateToken(token);
        ProtectedToken = TokenProtection.Protect(token);
    }

    public Uri Validate(bool requireToken = true)
    {
        if (!Uri.TryCreate(RouterUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("请输入完整的 HTTP 或 HTTPS 接口地址，不要包含用户名、密码或查询参数。");
        if (!IPAddress.TryParse(Client, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new FormatException("请输入这台 Windows 电脑的 IPv4 地址。");
        if (requireToken) ValidateToken(GetToken());
        return uri;
    }

    public static RouterSettings FromExportJson(string json)
    {
        if (json.Length > 65536) throw new FormatException("连接信息内容过大。");
        ExportedConnection? imported;
        try { imported = JsonSerializer.Deserialize<ExportedConnection>(json, JsonOptions); }
        catch (JsonException) { throw new FormatException("不是有效的连接信息 JSON，请从路由器网速统计页面重新复制或导出。"); }
        if (imported is null || string.IsNullOrWhiteSpace(imported.RouterUrl) ||
            string.IsNullOrWhiteSpace(imported.Client) || string.IsNullOrWhiteSpace(imported.Token))
            throw new FormatException("连接信息缺少 RouterUrl、Client 或 Token。");
        var settings = new RouterSettings { RouterUrl = imported.RouterUrl.Trim(), Client = imported.Client.Trim() };
        settings.Validate(requireToken: false);
        settings.SetToken(imported.Token);
        return settings;
    }

    public static RouterSettings FromExportFile(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new FormatException("连接信息文件过大。");
        return FromExportJson(File.ReadAllText(path));
    }

    private static void ValidateToken(string token)
    {
        if (token.Length is < 16 or > 512 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-._~+/=".Contains(c)))
            throw new FormatException("连接凭证无效，请从路由器网速统计页面重新复制或导出。");
    }

    private sealed record ExportedConnection(string? RouterUrl, string? Client, string? Token);
}

internal static class TokenProtection
{
    private const int UiForbidden = 1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RouterSpeed readonly API token v1");
    public static string Protect(string token)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(token);
        try { return Convert.ToBase64String(Transform(plaintext, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static string Unprotect(string encrypted)
    {
        byte[] plaintext = Transform(Convert.FromBase64String(encrypted), protect: false);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = Allocate(input);
        var entropy = Allocate(Entropy);
        DataBlob output = default;
        try
        {
            bool success = protect
                ? CryptProtectData(ref source, null, ref entropy, 0, 0, UiForbidden, out output)
                : CryptUnprotectData(ref source, 0, ref entropy, 0, 0, UiForbidden, out output);
            if (!success) throw new CryptographicException("无法使用当前 Windows 用户保护或读取连接凭证。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Clear(ref source, local: false);
            Clear(ref entropy, local: false);
            Clear(ref output, local: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var blob = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }
    private static void Clear(ref DataBlob blob, bool local)
    {
        if (blob.Data == 0) return;
        for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blob.Data, i, 0);
        if (local) LocalFree(blob.Data); else Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy,
        nint reserved, nint prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}

public sealed record Counters(long Timestamp, ulong DirectDown, ulong DirectUp, ulong ProxyDown,
    ulong ProxyUp, ulong UnknownDown, ulong UnknownUp, ulong DroppedPackets, string Client, uint MapId,
    string? InstanceId = null, string? Interface = null);

public sealed class RateCalculator
{
    // A few stray bytes (a fragment, a flow still waiting for its route) are normal; only
    // sustained unclassified traffic marks the interval, so the marker does not flicker.
    private const double UnclassifiedThreshold = 1024;
    private readonly string _routerHost;
    private Counters? _previous;
    private SpeedSnapshot? _lastSnapshot;
    public RateCalculator(string routerHost = "192.168.233.1") => _routerHost = routerHost;
    public void Reset() { _previous = null; _lastSnapshot = null; }
    public SpeedSnapshot Update(Counters next)
    {
        Counters? prev = _previous;
        if (next == prev && _lastSnapshot is not null) return _lastSnapshot;
        _previous = next;
        var detail = $"路由器 {_routerHost} · 本机 {next.Client} · IPv4 TCP/UDP\n" +
            "数据来自路由器只读统计接口、LAN 报文计数与 dae 分流记录。\n" +
            "计入 IP 头；不含内网访问、IPv6、无法解析的报文。未确认的出口不计入两类。";
        if (next.MapId == 0)
            return _lastSnapshot = new(0, 0, 0, 0, "分流映射不可用", detail + "\n等待 dae 内核映射恢复或检查版本兼容性。", false);
        if (prev is null || prev.MapId != next.MapId || prev.Client != next.Client ||
            prev.InstanceId != next.InstanceId || prev.Interface != next.Interface ||
            next.Timestamp <= prev.Timestamp || next.Timestamp - prev.Timestamp > 10000 ||
            next.DirectDown < prev.DirectDown || next.DirectUp < prev.DirectUp ||
            next.ProxyDown < prev.ProxyDown || next.ProxyUp < prev.ProxyUp ||
            next.UnknownDown < prev.UnknownDown || next.UnknownUp < prev.UnknownUp ||
            next.DroppedPackets < prev.DroppedPackets)
            return _lastSnapshot = new(0, 0, 0, 0, "正在建立基线", detail, false);
        double seconds = (next.Timestamp - prev.Timestamp) / 1000.0;
        double dd = (next.DirectDown - prev.DirectDown) / seconds;
        double du = (next.DirectUp - prev.DirectUp) / seconds;
        double pd = (next.ProxyDown - prev.ProxyDown) / seconds;
        double pu = (next.ProxyUp - prev.ProxyUp) / seconds;
        double ud = (next.UnknownDown - prev.UnknownDown) / seconds;
        double uu = (next.UnknownUp - prev.UnknownUp) / seconds;
        bool incomplete = ud + uu >= UnclassifiedThreshold || next.DroppedPackets > prev.DroppedPackets;
        string status = incomplete ? "部分流量未分类 · IPv4" : "已连接 · IPv4";
        detail += $"\n当前未分类：▼ {ud / 1024:0.0} KB/s  ▲ {uu / 1024:0.0} KB/s" +
            $"\n采集期间累计丢包：{next.DroppedPackets}。日志缺失或统计重连可能使分类不完整。";
        return _lastSnapshot = new(dd, du, pd, pu, status, detail, true);
    }
}

public sealed class RouterConnection : IDisposable
{
    private readonly RouterSettings _settings;
    private readonly RateCalculator _rates;
    private readonly HttpClient _http;
    private readonly Func<long> _clock;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private Counters? _latest;
    private long _receivedAt;
    private bool _disposed;

    public RouterConnection(RouterSettings settings) : this(settings, CreateHandler()) { }
    internal RouterConnection(RouterSettings settings, HttpMessageHandler handler, Func<long>? clock = null)
    {
        _settings = settings;
        _rates = new RateCalculator(Uri.TryCreate(settings.RouterUrl, UriKind.Absolute, out var url) ? url.Host : "未配置");
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBufferSize = 65536
        };
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    private static HttpClientHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    public async Task<SpeedSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(_settings.ProtectedToken))
            return Disconnected("尚未配置连接", "请右键打开连接设置，粘贴或导入路由器网速统计页面提供的连接信息。");
        try
        {
            var url = _settings.Validate();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.GetToken());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => Disconnected("连接凭证无效", "凭证已过期、被撤销或不正确。请从路由器网速统计页面重新复制连接信息。"),
                    HttpStatusCode.Forbidden => Disconnected("本机地址未获授权", "路由器限制了访问来源。请确认本机 IPv4 地址与路由器网速统计页面中的授权地址一致。"),
                    HttpStatusCode.ServiceUnavailable => Disconnected("采集器尚未就绪", "请在路由器网速统计页面查看采集器状态。程序会自动重试。"),
                    HttpStatusCode.NotFound => Disconnected("统计接口不存在", "请检查接口地址，并确认路由器已安装网速统计服务。"),
                    _ => Disconnected("统计接口暂不可用", "路由器接口未返回统计数据。请检查网速统计页面，程序会自动重试。")
                };
            }
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var next = JsonSerializer.Deserialize<Counters>(body, JsonOptions);
            if (next is null || next.Timestamp <= 0 || next.Client != _settings.Client)
                return Disconnected("统计数据不匹配", "接口没有返回这台电脑的有效计数。请检查连接设置中的本机 IPv4 地址。");
            long now = _clock();
            if (_latest != next)
            {
                _latest = next;
                _receivedAt = now;
            }
            else if (Stopwatch.GetElapsedTime(_receivedAt, now).TotalSeconds > 5)
            {
                _rates.Reset();
                return Offline("采集数据未更新", "路由器计数超过 5 秒没有更新。请在路由器网速统计页面检查采集器状态。");
            }
            return _rates.Update(next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CryptographicException) { return Disconnected("需要重新导入凭证", "当前 Windows 用户无法读取保存的凭证。请重新导入路由器提供的连接信息。"); }
        catch (FormatException) { return Disconnected("连接设置无效", "请右键打开连接设置，检查接口地址、本机 IPv4 和连接凭证。"); }
        catch (JsonException) { return Disconnected("统计数据格式异常", "接口返回的数据格式不正确。请确认连接的是路由器网速统计接口。"); }
        catch (OperationCanceledException) { return Disconnected("连接超时", "3 秒内未收到路由器响应。请检查本地网络，程序会自动重试。"); }
        catch (HttpRequestException) { return Disconnected("连接中断", "无法读取路由器统计接口。请检查本地网络和连接地址，程序会自动重试。"); }
        catch (ObjectDisposedException) { return Disconnected("正在更新连接", "连接设置已更新，正在重新读取统计数据。"); }
        catch { return Disconnected("连接中断", "暂时无法读取路由器统计数据。请检查连接设置，程序会自动重试。"); }
    }

    private SpeedSnapshot Disconnected(string status, string detail)
    {
        _latest = null;
        _receivedAt = 0;
        _rates.Reset();
        return Offline(status, detail);
    }
    private SpeedSnapshot Offline(string status, string detail) =>
        new(0, 0, 0, 0, status, $"本机 {_settings.Client}\n{detail}", false);
    public void Dispose() { if (_disposed) return; _disposed = true; _http.Dispose(); }
}
