namespace DeveloperPresence.Core.T3;

/// <summary>
/// Implements Most-Active-Now leader selection with sustained lead stability.
/// - Selects the agent tool with the most confirmed running tasks and helpers.
/// - Ties retain the current winner.
/// - A new winner needs a sustained 10-second lead unless the current winner has no running work.
/// - Fallbacks: all-waiting -> all-starting -> app-only fallback.
/// </summary>
public sealed class T3LeaderTracker
{
    private string? _currentLeaderName;
    private string? _challengerName;
    private DateTimeOffset? _challengerLeadStartedAt;

    public string? CurrentLeaderName => _currentLeaderName;
    public string? ChallengerName => _challengerName;
    public DateTimeOffset? ChallengerLeadStartedAt => _challengerLeadStartedAt;

    public T3HarnessWorkload? SelectLeader(IReadOnlyList<T3HarnessWorkload> workloads, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workloads);

        var runningWorkloads = workloads.Where(w => w.TotalRunning > 0).ToList();

        if (runningWorkloads.Count > 0)
        {
            int maxRunning = runningWorkloads.Max(w => w.TotalRunning);
            var topCandidates = runningWorkloads.Where(w => w.TotalRunning == maxRunning).ToList();

            var currentWinner = workloads.FirstOrDefault(w =>
                string.Equals(w.HarnessName, _currentLeaderName, StringComparison.OrdinalIgnoreCase));
            int currentWinnerRunning = currentWinner?.TotalRunning ?? 0;

            if (currentWinnerRunning > 0)
            {
                // Current leader has active running work
                if (topCandidates.Any(c => string.Equals(c.HarnessName, _currentLeaderName, StringComparison.OrdinalIgnoreCase)))
                {
                    // Ties retain the current winner (or current winner is ahead)
                    _challengerName = null;
                    _challengerLeadStartedAt = null;
                    return currentWinner;
                }

                // A challenger has strictly more running workers than current winner
                var candidate = topCandidates[0];
                if (!string.Equals(candidate.HarnessName, _challengerName, StringComparison.OrdinalIgnoreCase))
                {
                    // New challenger begins sustained lead challenge
                    _challengerName = candidate.HarnessName;
                    _challengerLeadStartedAt = now;
                    // Current winner retains badge during the 10-second window
                    return currentWinner;
                }
                else
                {
                    // Challenger has maintained its lead
                    if (_challengerLeadStartedAt.HasValue && now - _challengerLeadStartedAt.Value >= TimeSpan.FromSeconds(10))
                    {
                        // 10-second lead sustained: dethrone current winner!
                        _currentLeaderName = candidate.HarnessName;
                        _challengerName = null;
                        _challengerLeadStartedAt = null;
                        return candidate;
                    }

                    // Lead not yet sustained for 10s: current winner retains
                    return currentWinner;
                }
            }
            else
            {
                // Current winner has no running work (or no prior winner): take over immediately
                var candidate = topCandidates[0];
                _currentLeaderName = candidate.HarnessName;
                _challengerName = null;
                _challengerLeadStartedAt = null;
                return candidate;
            }
        }

        // No running work in any harness
        _currentLeaderName = null;
        _challengerName = null;
        _challengerLeadStartedAt = null;

        // Check waiting work
        var waitingWorkloads = workloads.Where(w => w.TotalWaiting > 0).ToList();
        if (waitingWorkloads.Count > 0)
        {
            int maxWaiting = waitingWorkloads.Max(w => w.TotalWaiting);
            return waitingWorkloads.First(w => w.TotalWaiting == maxWaiting);
        }

        // Check starting/queued work
        var startingWorkloads = workloads.Where(w => w.TotalStarting > 0).ToList();
        if (startingWorkloads.Count > 0)
        {
            int maxStarting = startingWorkloads.Max(w => w.TotalStarting);
            return startingWorkloads.First(w => w.TotalStarting == maxStarting);
        }

        // No active work at all
        return null;
    }

    public void Reset()
    {
        _currentLeaderName = null;
        _challengerName = null;
        _challengerLeadStartedAt = null;
    }
}
