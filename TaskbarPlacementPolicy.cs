using System.Drawing;

namespace RouterSpeed;

internal enum TaskbarPlacementAction { Dock, Keep, Wait, Float }

/// <summary>
/// A failed accessibility read is not evidence that taskbar space disappeared.
/// Use a monotonic clock and independent samples before changing presentation.
/// </summary>
internal sealed class TaskbarPlacementPolicy(Func<long>? clockMilliseconds = null)
{
    private readonly Func<long> _clock = clockMilliseconds ?? (() => Environment.TickCount64);
    private long? _unavailableSince;
    private long? _confirmedFailureSince;
    private int _failureSamples;
    private DateTimeOffset _lastFailureSample;
    private Rectangle? _candidate;
    private long _candidateSince;
    private int _candidateSamples;
    private DateTimeOffset _lastCandidateSample;
    private bool _recoveryRequired;

    internal void Reset()
    {
        _unavailableSince = null;
        _confirmedFailureSince = null;
        _failureSamples = 0;
        _lastFailureSample = default;
        _recoveryRequired = false;
        ResetCandidate();
    }

    internal TaskbarPlacementAction Evaluate(TaskbarDockSnapshot snapshot, Rectangle? target,
        bool docked, Rectangle currentBounds)
    {
        long now = _clock();
        if (snapshot.Covered)
        {
            // The shell raised its taskbar above the desktop band (Start menu, search, tray
            // flyouts). The overlay cannot be drawn above it and the layout has not changed,
            // so neither the confirmation timers nor the floating fallback may advance: a docked
            // bar simply waits underneath and is raised again the moment the taskbar comes back.
            ResetCandidate();
            _unavailableSince = null;
            _confirmedFailureSince = null;
            _failureSamples = 0;
            _lastFailureSample = default;
            return docked && !_recoveryRequired ? TaskbarPlacementAction.Keep : WaitForRecovery();
        }
        if (target is { } desired)
        {
            _unavailableSince = null;
            _confirmedFailureSince = null;
            _failureSamples = 0;
            _lastFailureSample = default;
            if (docked && desired == currentBounds && !_recoveryRequired)
            {
                ResetCandidate();
                return TaskbarPlacementAction.Dock;
            }
            if (_candidate != desired)
            {
                _candidate = desired;
                _candidateSince = now;
                _candidateSamples = 1;
                _lastCandidateSample = snapshot.CapturedAtUtc;
            }
            else if (snapshot.CapturedAtUtc != _lastCandidateSample)
            {
                _candidateSamples++;
                _lastCandidateSample = snapshot.CapturedAtUtc;
            }
            if (_candidateSamples >= 2 && now - _candidateSince >= 650)
            {
                _recoveryRequired = false;
                ResetCandidate();
                return TaskbarPlacementAction.Dock;
            }
            // Wait for a stable new anchor without obscuring controls which have moved.
            if (!docked || (!_recoveryRequired && snapshot.AvailableArea.Contains(currentBounds)))
                return TaskbarPlacementAction.Keep;
            return WaitForRecovery();
        }

        ResetCandidate();
        _unavailableSince ??= now;
        if (snapshot.IsAvailable || snapshot.LayoutConfirmed)
        {
            if (_confirmedFailureSince is null)
            {
                _confirmedFailureSince = now;
                _failureSamples = 1;
                _lastFailureSample = snapshot.CapturedAtUtc;
            }
            else if (snapshot.CapturedAtUtc != _lastFailureSample)
            {
                _failureSamples++;
                _lastFailureSample = snapshot.CapturedAtUtc;
            }
            if (_failureSamples >= 2 && now - _confirmedFailureSince >= 2000)
                return TaskbarPlacementAction.Float;
        }
        else
        {
            _confirmedFailureSince = null;
            _failureSamples = 0;
            _lastFailureSample = default;
        }
        // Persistent shell/accessibility failure still has a usable fallback.
        if (now - _unavailableSince >= 10000) return TaskbarPlacementAction.Float;
        if (!docked || (!_recoveryRequired && snapshot.CanKeepPlacement && snapshot.AvailableArea.Contains(currentBounds)))
            return TaskbarPlacementAction.Keep;
        return WaitForRecovery();
    }

    private TaskbarPlacementAction WaitForRecovery()
    {
        _recoveryRequired = true;
        return TaskbarPlacementAction.Wait;
    }

    private void ResetCandidate()
    {
        _candidate = null;
        _candidateSamples = 0;
        _lastCandidateSample = default;
    }
}
