using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RouterSpeed;

// Isolated experiment: every manipulated HWND belongs to this executable or its
// explicitly created child. The real taskbar and Explorer are never addressed.
internal static class EventLatencyExperiment
{
    private const uint Flags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
    private static readonly uint[] Events = [0x0003, 0x8002, 0x8003, 0x8004, 0x800B];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static int Run(bool productionObserver = false)
    {
        using var overlay = new TransparentWindow();
        int exit = 1;
        overlay.Shown += async (_, _) =>
        {
            try { exit = await RunAsync(overlay, productionObserver); }
            catch (Exception ex) { Console.WriteLine("Experiment failed: " + ex.GetType().Name); }
            finally { overlay.Close(); }
        };
        Application.Run(overlay);
        return exit;
    }

    private static async Task<int> RunAsync(TransparentWindow overlay, bool productionObserver)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(EventLatencyExperiment).Assembly.Location);
        start.ArgumentList.Add("--event-source");
        using Process source = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated source.");
        var hooks = new List<nint>();
        var trials = new List<Trial>();
        var unrelatedEvents = new Dictionary<uint, int>();
        var errors = new List<string>();
        var lifecycleChecks = new List<object>();
        TaskbarOrderObserver? observer = null;
        int observerCallbacks = 0;
        Trial? current = null;
        SourceInfo? info = null;
        bool repairQueued = false;
        string mode = "none";
        nint originalForeground = Native.GetForegroundWindow();
        int overlayVisibleChanges = 0;
        overlay.VisibleChanged += (_, _) => overlayVisibleChanges++;
        void Repair(string reason)
        {
            Trial? trial = current;
            if (trial is null || trial.RepairAt != 0 || info is null) return;
            if (Above(overlay.Handle, (nint)info.Target)) return;
            bool success = TaskbarWindowOrder.EnsureAbove(overlay.Handle, (nint)info.Target);
            trial.Attempts.Add(new(reason, Stopwatch.GetTimestamp(), success));
            if (!success || !Above(overlay.Handle, (nint)info.Target)) return;
            trial.RepairAt = Stopwatch.GetTimestamp();
            trial.RepairBy = reason;
            trial.PopupAboveAtRepair = Above((nint)info.Popup, overlay.Handle);
            trial.Completed.TrySetResult();
        }
        WinEvent callback = (_, eventId, hwnd, objectId, childId, threadId, nativeEventTime) =>
        {
            if (info is null) return;
            bool testWindow = hwnd == (nint)info.Target || hwnd == (nint)info.Popup;
            bool sourceThread = threadId == info.Thread;
            if (!testWindow && !sourceThread)
            {
                unrelatedEvents[eventId] = unrelatedEvents.GetValueOrDefault(eventId) + 1;
                return;
            }
            Trial? trial = current;
            if (trial is null) return;
            trial.Events.Add(new(eventId, hwnd == (nint)info.Target ? "target" : hwnd == (nint)info.Popup ? "popup" : "source-other",
                objectId, childId, Stopwatch.GetTimestamp(), nativeEventTime));
            if (mode != "event" || repairQueued) return;
            repairQueued = true;
            overlay.BeginInvoke(() => { repairQueued = false; Repair("event"); });
        };
        using var timer = new System.Windows.Forms.Timer { Interval = 350 };
        timer.Tick += (_, _) => Repair("timer");
        void Verify(string name, bool passed)
        {
            lifecycleChecks.Add(new { name, passed });
            if (!passed) errors.Add(name);
        }
        try
        {
            string handshake = await source.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("No handshake.");
            info = JsonSerializer.Deserialize<SourceInfo>(handshake) ?? throw new IOException("Invalid handshake.");
            if (info.Process != source.Id) throw new InvalidOperationException("Unexpected test process.");
            Native.GetWindowThreadProcessId((nint)info.Target, out uint targetProcess);
            Native.GetWindowThreadProcessId((nint)info.Popup, out uint popupProcess);
            if (targetProcess != source.Id || popupProcess != source.Id) throw new InvalidOperationException("Foreign HWND rejected.");
            foreach (uint eventId in Events)
            {
                nint hook = Native.SetWinEventHook(eventId, eventId, 0, callback, 0, 0, 0x0002); // OUTOFCONTEXT | SKIPOWNPROCESS
                if (hook == 0) throw new InvalidOperationException("WinEvent hook failed.");
                hooks.Add(hook);
            }
            async Task TrialAsync(string trialMode, int index)
            {
                var trial = new Trial { Mode = trialMode, Index = index, RequestedAt = Stopwatch.GetTimestamp() };
                current = trial;
                Raised raised = await Raise(source, overlay.Handle, deadline.Token);
                trial.RaiseAt = raised.RaiseAt;
                trial.SourceRaiseSucceeded = raised.Success;
                trial.SourceObservedBelow = raised.OverlayBelow;
                await Task.WhenAny(trial.Completed.Task, Task.Delay(900, deadline.Token));
                await Task.Delay(25, deadline.Token);
                trial.FinalAbove = Above(overlay.Handle, (nint)info.Target);
                trial.FinalVisible = overlay.Visible;
                trial.PopupPreserved = Above((nint)info.Popup, overlay.Handle);
                trials.Add(trial);
                if (!raised.Success || !trial.FinalAbove || trial.RepairAt == 0 || !trial.PopupPreserved)
                    errors.Add($"{trialMode}:{index}:incomplete-or-order-changed");
                current = null;
            }
            if (productionObserver)
            {
                observer = new TaskbarOrderObserver(overlay, () => { observerCallbacks++; Repair("observer"); }, () => (nint)info.Target);
                Verify("Construction does not start hooks", !observer.IsBound);
                observer.Start();
                Verify("Start binds the cross-process source", observer.IsBound);
            }
            foreach (string experimentMode in productionObserver ? new[] { "observer" } : new[] { "timer", "event" })
            {
                mode = experimentMode;
                if (!productionObserver) timer.Start();
                for (int i = 0; i < 12; i++)
                {
                    current = null;
                    await Raise(source, overlay.Handle, deadline.Token);
                    if (!TaskbarWindowOrder.EnsureAbove(overlay.Handle, (nint)info.Target)) throw new InvalidOperationException("Baseline repair failed.");
                    await Task.Delay(35 + i * 17 % 120, deadline.Token); // Distinct phases of the 350ms timer.
                    await TrialAsync(mode, i + 1);
                }
                timer.Stop();
                current = null;
            }
            if (observer is not null)
            {
                int requests = overlay.PositionRequests, callbacks = observerCallbacks;
                for (int i = 0; i < 30; i++) { observer.Start(); observer.RefreshBinding(); }
                await Task.Delay(80, deadline.Token);
                Verify("Stable Start and RefreshBinding do not adjust Z order", overlay.PositionRequests == requests);
                Verify("Stable Start and RefreshBinding do not dispatch callbacks", observerCallbacks == callbacks);

                observer.Stop(); observer.Stop();
                Verify("Stop is idempotent and unbinds", !observer.IsBound);
                await Raise(source, overlay.Handle, deadline.Token);
                await Task.Delay(100, deadline.Token);
                Verify("Stopped observer does not repair or callback", observerCallbacks == callbacks && !Above(overlay.Handle, (nint)info.Target));
                TaskbarWindowOrder.EnsureAbove(overlay.Handle, (nint)info.Target);
                observer.Start();
                await TrialAsync("observer-restart", 1);
                Verify("Restart binds and repairs genuine native reorder", observer.IsBound && trials[^1].RepairBy == "observer");

                // Deterministic generation test: simulate a delivered hook callback
                // but stop before the queued UI delegate can run. No global event is emitted.
                var binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                nint objectHook = (nint)typeof(TaskbarOrderObserver).GetField("_objectHook", binding)!.GetValue(observer)!;
                typeof(TaskbarOrderObserver).GetMethod("OnWinEvent", binding)!.Invoke(observer,
                    [objectHook, (uint)0x8004, (nint)info.Target, -4, 0, info.Thread, (uint)0]);
                callbacks = observerCallbacks;
                observer.Stop();
                await Task.Delay(50, deadline.Token);
                Verify("Stop invalidates the queued previous-generation callback", observerCallbacks == callbacks);
                observer.Start();
                await TrialAsync("observer-after-stale-queue", 1);
                Verify("Old queue does not block a restarted observer", trials[^1].RepairBy == "observer");

                overlay.RecreateForTest();
                Verify("Dispatcher handle recreation rebinds hooks", observer.IsBound);
                await TrialAsync("observer-after-handle-recreation", 1);
                Verify("Recreated dispatcher repairs genuine native reorder", trials[^1].RepairBy == "observer");

                observer.Dispose(); observer.Dispose(); observer.Start(); observer.RefreshBinding();
                callbacks = observerCallbacks;
                await Raise(source, overlay.Handle, deadline.Token);
                await Task.Delay(100, deadline.Token);
                Verify("Dispose is terminal and no callback survives", !observer.IsBound && callbacks == observerCallbacks && !Above(overlay.Handle, (nint)info.Target));
            }
            if (Native.GetForegroundWindow() != originalForeground) errors.Add("foreground-changed");
            if (overlayVisibleChanges != 0) errors.Add("overlay-visibility-changed");
            var summaries = trials.GroupBy(t => t.Mode).Select(group =>
            {
                double[] latencies = group.Where(t => t.RepairAt != 0).Select(t => Milliseconds(t.RepairAt - t.RaiseAt)).Order().ToArray();
                return new
                {
                    mode = group.Key, trials = group.Count(), repaired = latencies.Length,
                    eventRepaired = group.Count(t => t.RepairBy == "event"), timerRepaired = group.Count(t => t.RepairBy == "timer"),
                    observerRepaired = group.Count(t => t.RepairBy == "observer"),
                    minimumMs = latencies.DefaultIfEmpty().Min(), medianMs = latencies.Length == 0 ? 0 : latencies[latencies.Length / 2],
                    maximumMs = latencies.DefaultIfEmpty().Max(),
                    eventTypes = group.SelectMany(t => t.Events).GroupBy(e => e.EventId).Select(g => new { id = $"0x{g.Key:X4}", count = g.Count() }).ToArray()
                };
            }).ToArray();
            string report = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, productionObserver
                ? "../../../observer-integration-results.json" : "../../../event-latency-results.json"));
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                experiment = "Native SetWindowPos on isolated transparent cross-process test windows; no synthetic NotifyWinEvent",
                sourceRaiseMode = "Target and then popup raised separately; popup order asserted after source acknowledgment and event processing",
                qpcFrequency = Stopwatch.Frequency, sourceProcess = source.Id, parentProcess = Environment.ProcessId,
                sourceThread = info.Thread, originalForegroundWasNull = originalForeground == 0,
                foregroundPreserved = Native.GetForegroundWindow() == originalForeground, overlayVisibleChanges,
                summaries, errors, observerCallbacks, lifecycleChecks,
                trials = trials.Select(t => new
                {
                    t.Mode, t.Index, t.SourceRaiseSucceeded, t.SourceObservedBelow, t.FinalAbove, t.FinalVisible, t.PopupAboveAtRepair, t.PopupPreserved,
                    t.RepairBy, repairLatencyMs = t.RepairAt == 0 ? (double?)null : Milliseconds(t.RepairAt - t.RaiseAt),
                    events = t.Events.Select(e => new { id = $"0x{e.EventId:X4}", e.Window, e.ObjectId, e.ChildId,
                        deliveryAfterRaiseMs = Milliseconds(e.DeliveredAt - t.RaiseAt), e.NativeEventTime }),
                    attempts = t.Attempts.Select(a => new { a.Reason, a.Success, afterRaiseMs = Milliseconds(a.At - t.RaiseAt) })
                })
            }, Json));
            Console.WriteLine(JsonSerializer.Serialize(new { summaries, lifecycleChecks, errors, report }, Json));
            return errors.Count == 0 ? 0 : 1;
        }
        finally
        {
            current = null;
            timer.Stop();
            observer?.Dispose();
            foreach (nint hook in hooks) Native.UnhookWinEvent(hook);
            GC.KeepAlive(callback);
            try { await source.StandardInput.WriteLineAsync("quit"); } catch { }
            if (!source.WaitForExit(1500)) source.Kill(); // Only the child created above.
        }
    }

    private static async Task<Raised> Raise(Process source, nint overlay, CancellationToken token)
    {
        await source.StandardInput.WriteLineAsync($"raise:{overlay.ToInt64()}:{Environment.ProcessId}");
        string result = await source.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Source exited.");
        return JsonSerializer.Deserialize<Raised>(result) ?? throw new IOException("Invalid response.");
    }

    internal static int RunSource()
    {
        using var target = new TransparentWindow();
        using var popup = new TransparentWindow();
        target.Show(); popup.Show();
        Console.WriteLine(JsonSerializer.Serialize(new SourceInfo(target.Handle.ToInt64(), popup.Handle.ToInt64(), Environment.ProcessId, Native.GetCurrentThreadId())));
        Console.Out.Flush();
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } command)
            {
                if (command == "quit") { target.BeginInvoke(target.Close); return; }
                string[] parts = command.Split(':');
                if (parts.Length != 3 || parts[0] != "raise" || !long.TryParse(parts[1], out long overlay) ||
                    !uint.TryParse(parts[2], out uint parentProcess)) continue;
                target.BeginInvoke(() =>
                {
                    Native.GetWindowThreadProcessId((nint)overlay, out uint actualProcess);
                    if (actualProcess != parentProcess) throw new InvalidOperationException("Foreign overlay HWND rejected.");
                    long timestamp = Stopwatch.GetTimestamp();
                    bool success = Native.SetWindowPos(target.Handle, new nint(-1), 0, 0, 0, 0, Flags) &&
                        Native.SetWindowPos(popup.Handle, new nint(-1), 0, 0, 0, 0, Flags);
                    Console.WriteLine(JsonSerializer.Serialize(new Raised(timestamp, success, !Above((nint)overlay, target.Handle))));
                    Console.Out.Flush();
                });
            }
            if (!target.IsDisposed) target.BeginInvoke(target.Close);
        });
        using var watchdog = new System.Windows.Forms.Timer { Interval = 45000 };
        watchdog.Tick += (_, _) => target.Close(); watchdog.Start();
        Application.Run(target);
        return 0;
    }

    private static bool Above(nint upper, nint lower)
    {
        var seen = new HashSet<nint>();
        for (nint next = Native.GetWindow(lower, 3); next != 0 && seen.Count < 4096; next = Native.GetWindow(next, 3))
        {
            if (!seen.Add(next)) return false;
            if (next == upper) return true;
        }
        return false;
    }
    private static double Milliseconds(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 3);
    private sealed record SourceInfo(long Target, long Popup, int Process, uint Thread);
    private sealed record Raised(long RaiseAt, bool Success, bool OverlayBelow);
    private sealed record EventData(uint EventId, string Window, int ObjectId, int ChildId, long DeliveredAt, uint NativeEventTime);
    private sealed record Attempt(string Reason, long At, bool Success);
    private sealed class Trial
    {
        internal string Mode = "", RepairBy = "none";
        internal int Index;
        internal long RequestedAt, RaiseAt, RepairAt;
        internal bool SourceRaiseSucceeded, SourceObservedBelow, FinalAbove, FinalVisible, PopupAboveAtRepair, PopupPreserved;
        internal List<EventData> Events = [];
        internal List<Attempt> Attempts = [];
        internal TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class TransparentWindow : Form
    {
        internal int PositionRequests;
        internal void RecreateForTest() => RecreateHandle();
        internal TransparentWindow()
        {
            Text = "RouterSpeed isolated event experiment";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(0, 0, 2, 2); Opacity = 0; TopMost = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000020; return value; } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0046) PositionRequests++;
            base.WndProc(ref message);
        }
    }
    private delegate void WinEvent(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint threadId, uint time);
    private static class Native
    {
        [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}
