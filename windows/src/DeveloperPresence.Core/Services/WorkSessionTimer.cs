using DeveloperPresence.Core.Models;

namespace DeveloperPresence.Core.Services;

/// <summary>Immutable view of the two clocks a card can show (spec/presence-v3.md §5.5).</summary>
public readonly record struct PresenceTimerState(DateTimeOffset? WorkSessionStart, DateTimeOffset? AppShownStart);

/// <summary>
/// Tracks the work-session clock and the app clock.
/// - App clock: when the shown app became the shown app.
/// - Work session: starts at the first fresh snapshot with unfinished work, survives
///   running ↔ waiting ↔ starting, and ends after 120 seconds without unfinished work
///   or when activity sharing stops.
/// Not thread-safe; the coordinator calls it from its single evaluation worker.
/// </summary>
public sealed class WorkSessionTimer
{
    public static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(120);

    private DateTimeOffset? _sessionStart;
    private DateTimeOffset? _lastUnfinishedWorkAt;
    private SupportedApp _shownApp = SupportedApp.None;
    private DateTimeOffset? _appShownStart;

    public PresenceTimerState State => new(_sessionStart, _appShownStart);
    public DateTimeOffset? WorkSessionStart => _sessionStart;
    public DateTimeOffset? AppShownStart => _appShownStart;

    /// <summary>Records one evaluation. Pass hasUnfinishedWork only for a fresh snapshot.</summary>
    public PresenceTimerState Observe(bool hasUnfinishedWork, DateTimeOffset now)
    {
        if (hasUnfinishedWork)
        {
            _sessionStart ??= now;
            _lastUnfinishedWorkAt = now;
        }
        else if (_sessionStart != null && (_lastUnfinishedWorkAt == null || now - _lastUnfinishedWorkAt.Value >= SessionGrace))
        {
            EndSession();
        }
        return State;
    }

    /// <summary>Records which app the card shows. A change restarts the app clock.</summary>
    public PresenceTimerState ObserveShownApp(SupportedApp app, DateTimeOffset now)
    {
        if (app == SupportedApp.None)
        {
            _shownApp = SupportedApp.None;
            _appShownStart = null;
        }
        else if (app != _shownApp || _appShownStart == null)
        {
            _shownApp = app;
            _appShownStart = now;
        }
        return State;
    }

    /// <summary>Activity sharing stopped (off, paused, locked, asleep, T3 quit, activity hidden).</summary>
    public void EndSession()
    {
        _sessionStart = null;
        _lastUnfinishedWorkAt = null;
    }

    public void Reset()
    {
        EndSession();
        _shownApp = SupportedApp.None;
        _appShownStart = null;
    }
}
