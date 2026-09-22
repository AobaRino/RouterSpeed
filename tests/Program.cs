using RouterSpeed;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

var cases = new (string Name, Action Run)[]
{
    ("First sample establishes a baseline", FirstSample),
    ("Rates use elapsed router time and independent counters", RatesUseElapsedTime),
    ("Repeated samples retain the last valid rates", RepeatedSample),
    ("Repeated initial samples retain the baseline", RepeatedBaseline),
    ("New idle samples display connected zero", IdleSample),
    ("Reset discards the old rate and baseline", ExplicitReset),
    ("All counter decreases establish a new baseline", CounterResets),
    ("Map and client changes establish a new baseline", IdentityChanges),
    ("A missing routing map stays disconnected", MissingMap),
    ("Long gaps establish a new baseline", LongGap),
    ("Clock rollback establishes a new baseline", ClockRollback),
    ("Changed counters at the same timestamp cannot create a rate", SameTimestampMutation),
    ("Unclassified bytes are excluded from direct and proxy", UnclassifiedBytes),
    ("Packet loss marks classification as incomplete", PacketLoss),
    ("Historic unknown counts do not mark a later clean interval", HistoricUnknown),
    ("Stray unclassified bytes below 1 KB/s do not mark the interval", StrayUnknown),
    ("Details identify the configured router", ConfiguredHost),
    ("Polling honors cancellation before attempting HTTP", CancelBeforeConnect),
    ("Collector process changes reset even increasing counters", InstanceChanges),
    ("Imported tokens round-trip through DPAPI without plaintext persistence", ProtectedSettingsRoundTrip),
    ("Malformed exports and credential-bearing URLs are rejected", InvalidImports),
    ("Tampered protected tokens cannot be decrypted", TamperedToken),
    ("HTTP requests send only the readonly bearer credential", HttpAuthorization),
    ("HTTP authorization and service errors remain distinct and sanitized", HttpErrors),
    ("HTTP errors reset the next measurement baseline", HttpRecovery),
    ("Unchanged HTTP samples expire and recover without stale rates", HttpStaleness),
    ("Another client's counters are rejected", WrongClient),
    ("Oversized HTTP responses are rejected", OversizedResponse),
    ("Invalid HTTP JSON is rejected without exposing the response", MalformedResponse),
};

int failed = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} passed");
return failed == 0 ? 0 : 1;

static Counters Baseline() => new(1000, 100, 200, 300, 400, 500, 600, 7, "192.168.233.10", 42);
static void MissingMap()
{
    var calculator = new RateCalculator();
    var sample = Baseline() with { MapId = 0 };
    Offline(calculator.Update(sample));
    Offline(calculator.Update(sample with { Timestamp = 2000 }));
    Offline(calculator.Update(sample with { Timestamp = 3000, MapId = 42 }));
    Check(calculator.Update(sample with { Timestamp = 4000, MapId = 42 }).Connected, "Map recovery should resume after a baseline.");
}
static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
static void Near(double expected, double actual)
    => Check(double.IsFinite(actual) && Math.Abs(expected - actual) < .000001, $"Expected {expected}, got {actual}.");
static void Offline(SpeedSnapshot snapshot)
{
    Check(!snapshot.Connected, "Must not present a valid rate without a baseline.");
    Near(0, snapshot.DirectDown); Near(0, snapshot.DirectUp);
    Near(0, snapshot.ProxyDown); Near(0, snapshot.ProxyUp);
}

static void FirstSample() => Offline(new RateCalculator().Update(Baseline()));

static void RatesUseElapsedTime()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var result = calculator.Update(first with { Timestamp = 3500, DirectDown = 2600, DirectUp = 700, ProxyDown = 5300, ProxyUp = 650 });
    Check(result.Connected, "A second valid sample must be connected.");
    Near(1000, result.DirectDown); Near(200, result.DirectUp);
    Near(2000, result.ProxyDown); Near(100, result.ProxyUp);
}

static void RepeatedSample()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var next = first with { Timestamp = 2000, DirectDown = 1124, UnknownUp = 1624 };
    var valid = calculator.Update(next);
    for (int i = 0; i < 3; i++)
        Check(calculator.Update(next) == valid, "Repeated polling changed a valid displayed rate or classification.");
    var third = calculator.Update(next with { Timestamp = 3000, DirectDown = 3172 });
    Near(2048, third.DirectDown);
}

static void RepeatedBaseline()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    var baseline = calculator.Update(first);
    Check(calculator.Update(first) == baseline, "An identical first sample must preserve baseline state.");
    Offline(baseline);
}

static void IdleSample()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var result = calculator.Update(first with { Timestamp = 2000 });
    Check(result.Connected, "A new sample with unchanged counters is a valid idle interval.");
    Near(0, result.DirectDown); Near(0, result.ProxyDown);
}

static void ExplicitReset()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var next = first with { Timestamp = 2000, DirectDown = 1124 };
    Check(calculator.Update(next).Connected, "Expected a valid interval.");
    calculator.Reset();
    Offline(calculator.Update(next));
    Near(1024, calculator.Update(next with { Timestamp = 3000, DirectDown = 2148 }).DirectDown);
}

static void CounterResets()
{
    var first = Baseline();
    Counters[] resets =
    [
        first with { DirectDown = 0 }, first with { DirectUp = 0 },
        first with { ProxyDown = 0 }, first with { ProxyUp = 0 },
        first with { UnknownDown = 0 }, first with { UnknownUp = 0 },
        first with { DroppedPackets = 0 }
    ];
    foreach (var reset in resets)
    {
        var calculator = new RateCalculator();
        calculator.Update(first);
        var next = reset with { Timestamp = 2000 };
        Offline(calculator.Update(next));
        var recovered = calculator.Update(next with { Timestamp = 3000, DirectDown = next.DirectDown + 1024 });
        Check(recovered.Connected, "A counter reset must recover on the next valid sample.");
        Near(1024, recovered.DirectDown);
    }
}

static void IdentityChanges()
{
    var first = Baseline();
    foreach (var next in new[] { first with { Timestamp = 2000, MapId = 43 }, first with { Timestamp = 2000, Client = "192.168.233.11" } })
    {
        var calculator = new RateCalculator();
        calculator.Update(first);
        Offline(calculator.Update(next));
    }
}

static void LongGap()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var next = first with { Timestamp = 11001, DirectDown = 200000 };
    Offline(calculator.Update(next));
    Near(1024, calculator.Update(next with { Timestamp = 12001, DirectDown = 201024 }).DirectDown);
}

static void ClockRollback()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var next = first with { Timestamp = 999, DirectDown = 200 };
    Offline(calculator.Update(next));
    Near(1024, calculator.Update(next with { Timestamp = 1999, DirectDown = 1224 }).DirectDown);
}

static void SameTimestampMutation()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var changed = first with { DirectDown = 200 };
    Offline(calculator.Update(changed));
    Near(1024, calculator.Update(changed with { Timestamp = 2000, DirectDown = 1224 }).DirectDown);
}

static void UnclassifiedBytes()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var result = calculator.Update(first with { Timestamp = 2000, DirectDown = 1124, ProxyDown = 2348, UnknownDown = 8692, UnknownUp = 1624 });
    Check(result.Connected && result.Status.Contains("未分类"), "Unknown bytes need a visible partial-classification status.");
    Near(1024, result.DirectDown); Near(2048, result.ProxyDown);
    Near(0, result.DirectUp); Near(0, result.ProxyUp);
}

static void PacketLoss()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var result = calculator.Update(first with { Timestamp = 2000, DroppedPackets = 8 });
    Check(result.Connected && result.Status.Contains("未分类"), "Packet loss must mark the interval incomplete.");
}

static void HistoricUnknown()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var result = calculator.Update(first with { Timestamp = 2000 });
    Check(result.Connected && !result.Status.Contains("未分类"), "Only the current interval should determine partial classification.");
}
static void StrayUnknown()
{
    var calculator = new RateCalculator();
    var first = Baseline();
    calculator.Update(first);
    var quiet = calculator.Update(first with { Timestamp = 2000, UnknownDown = first.UnknownDown + 1023 });
    Check(quiet.Connected && !quiet.Status.Contains("未分类"), "1023 B/s of unknown traffic should not mark the interval.");
    var busy = calculator.Update(first with { Timestamp = 3000, UnknownDown = first.UnknownDown + 1023 + 1024 });
    Check(busy.Connected && busy.Status.Contains("未分类"), "1 KB/s of unknown traffic should mark the interval.");
}

static void ConfiguredHost()
{
    var calculator = new RateCalculator("192.0.2.1");
    Check(calculator.Update(Baseline()).Detail.Contains("192.0.2.1"), "Details must use the configured host.");
}

static void CancelBeforeConnect()
{
    using var connection = new RouterConnection(new RouterSettings());
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { connection.PollAsync(cancellation.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Cancellation was ignored.");
}

static void InstanceChanges()
{
    var calculator = new RateCalculator();
    var initial = Baseline() with { InstanceId = "collector-a", Interface = "br-lan" };
    calculator.Update(initial);
    Check(calculator.Update(initial with { Timestamp = 2000, DirectDown = 1124 }).Connected, "Expected a valid first process rate.");
    var restarted = initial with { Timestamp = 3000, InstanceId = "collector-b", DirectDown = 1048576 };
    Offline(calculator.Update(restarted));
    Near(1024, calculator.Update(restarted with { Timestamp = 4000, DirectDown = 1049600 }).DirectDown);
}

static RouterSettings TestSettings()
{
    var settings = new RouterSettings();
    settings.SetToken("test-only-credential-0123456789abcdef");
    return settings;
}

static void ProtectedSettingsRoundTrip()
{
    const string token = "test-only-credential-0123456789abcdef";
    var settings = RouterSettings.FromExportJson(JsonSerializer.Serialize(new
    {
        RouterUrl = "http://192.168.233.1/cgi-bin/router-speed", Client = "192.168.233.10", Token = token
    }));
    Check(settings.GetToken() == token, "Imported token must decrypt for the current Windows user.");
    Check(settings.ProtectedToken != token && !settings.ProtectedToken.Contains(token), "Stored ciphertext must not be plaintext.");
    string file = Path.Combine(Path.GetTempPath(), $"routerspeed-test-{Guid.NewGuid():N}.json");
    try
    {
        settings.Save(file);
        string text = File.ReadAllText(file);
        Check(!text.Contains(token) && !text.Contains("\"Token\""), "Saved JSON must contain no plaintext token property or value.");
        var restored = RouterSettings.Load(file);
        Check(restored.RouterUrl == settings.RouterUrl && restored.Client == settings.Client && restored.GetToken() == token,
            "Saved connection settings must round-trip.");
    }
    finally { if (File.Exists(file)) File.Delete(file); }
}

static void InvalidImports()
{
    foreach (string json in new[]
    {
        "not-json", "{}",
        "{\"RouterUrl\":\"http://user:password@192.168.233.1/cgi-bin/router-speed\",\"Client\":\"192.168.233.10\",\"Token\":\"test-token-1234567890\"}",
        "{\"RouterUrl\":\"http://192.168.233.1/cgi-bin/router-speed?token=123\",\"Client\":\"192.168.233.10\",\"Token\":\"test-token-1234567890\"}",
        "{\"RouterUrl\":\"file:///secret\",\"Client\":\"192.168.233.10\",\"Token\":\"test-token-1234567890\"}",
        "{\"RouterUrl\":\"http://192.168.233.1/cgi-bin/router-speed\",\"Client\":\"::1\",\"Token\":\"test-token-1234567890\"}"
    })
    {
        bool rejected = false;
        try { RouterSettings.FromExportJson(json); }
        catch (FormatException) { rejected = true; }
        Check(rejected, "Malformed or credential-bearing connection information must be rejected.");
    }
}

static void TamperedToken()
{
    var settings = TestSettings();
    var bytes = Convert.FromBase64String(settings.ProtectedToken);
    bytes[^1] ^= 0xff;
    settings.ProtectedToken = Convert.ToBase64String(bytes);
    try { settings.GetToken(); }
    catch (CryptographicException) { return; }
    throw new InvalidOperationException("Tampered ciphertext was accepted.");
}

static HttpResponseMessage JsonResponse(Counters counters) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(counters, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
};
static SpeedSnapshot Poll(RouterConnection connection) => connection.PollAsync(CancellationToken.None).GetAwaiter().GetResult();

static void HttpAuthorization()
{
    int requested = 0;
    using var connection = new RouterConnection(TestSettings(), new StubHandler(request =>
    {
        requested++;
        Check(request.Method == HttpMethod.Get, "Statistics must be requested read-only.");
        Check(request.RequestUri!.Query == "" && request.RequestUri.UserInfo == "", "Credentials must not enter the URL.");
        Check(request.Headers.Authorization?.Scheme == "Bearer" &&
            request.Headers.Authorization.Parameter == "test-only-credential-0123456789abcdef", "The bearer credential must be present.");
        return JsonResponse(Baseline());
    }));
    Offline(Poll(connection));
    Check(requested == 1, "Expected one statistics request.");
}

static void HttpErrors()
{
    foreach (var pair in new[]
    {
        (HttpStatusCode.Unauthorized, "连接凭证无效"), (HttpStatusCode.Forbidden, "本机地址未获授权"),
        (HttpStatusCode.ServiceUnavailable, "采集器尚未就绪"), (HttpStatusCode.Redirect, "统计接口暂不可用")
    })
    {
        using var connection = new RouterConnection(TestSettings(), new StubHandler(_ => new(pair.Item1)
        {
            Content = new StringContent("sensitive-server-message test-only-credential-0123456789abcdef")
        }));
        var result = Poll(connection);
        Offline(result);
        Check(result.Status == pair.Item2, "Status must identify the actionable connection error.");
        Check(!result.Detail.Contains("sensitive-server-message") && !result.Detail.Contains("test-only-credential"), "Server response bodies must not enter UI messages.");
    }
}

static void HttpRecovery()
{
    int sample = 0;
    using var connection = new RouterConnection(TestSettings(), new StubHandler(_ =>
    {
        sample++;
        if (sample == 3) return new(HttpStatusCode.ServiceUnavailable);
        return JsonResponse(Baseline() with { Timestamp = sample * 1000, DirectDown = (ulong)(sample * 1024) });
    }));
    Offline(Poll(connection));
    Near(1024, Poll(connection).DirectDown);
    Offline(Poll(connection));
    Offline(Poll(connection));
    Near(1024, Poll(connection).DirectDown);
}

static void HttpStaleness()
{
    long clock = 0;
    var sample = Baseline();
    using var connection = new RouterConnection(TestSettings(), new StubHandler(_ => JsonResponse(sample)), () => clock);
    Offline(Poll(connection));
    sample = sample with { Timestamp = 2000, DirectDown = 1124 };
    clock = Stopwatch.Frequency;
    Near(1024, Poll(connection).DirectDown);
    clock = Stopwatch.Frequency * 2;
    Near(1024, Poll(connection).DirectDown);
    clock = Stopwatch.Frequency * 7;
    var stale = Poll(connection);
    Offline(stale);
    Check(stale.Status == "采集数据未更新", "Unchanged data must expire using local monotonic time.");
    clock = Stopwatch.Frequency * 8;
    Offline(Poll(connection));
    sample = sample with { Timestamp = 3000, DirectDown = 2148 };
    Offline(Poll(connection));
    sample = sample with { Timestamp = 4000, DirectDown = 3172 };
    Near(1024, Poll(connection).DirectDown);
}

static void WrongClient()
{
    using var connection = new RouterConnection(TestSettings(), new StubHandler(_ => JsonResponse(Baseline() with { Client = "192.168.233.99" })));
    var result = Poll(connection);
    Offline(result);
    Check(result.Status == "统计数据不匹配", "Another device's rates must never be displayed.");
}

static void OversizedResponse()
{
    using var connection = new RouterConnection(TestSettings(), new StubHandler(_ => new(HttpStatusCode.OK)
    { Content = new StringContent(new string('x', 65537)) }));
    Offline(Poll(connection));
}

static void MalformedResponse()
{
    using var connection = new RouterConnection(TestSettings(), new StubHandler(_ => new(HttpStatusCode.OK)
    { Content = new StringContent("{token: 'sensitive-server-message'}") }));
    var result = Poll(connection);
    Offline(result);
    Check(result.Status == "统计数据格式异常" && !result.Detail.Contains("sensitive-server-message"), "Malformed JSON must fail without exposing its contents.");
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
