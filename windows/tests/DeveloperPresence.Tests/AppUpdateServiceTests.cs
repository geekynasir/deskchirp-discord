using DeveloperPresence.Core.Services;
using Xunit;

namespace DeveloperPresence.Tests;

public class AppUpdateServiceTests
{
    private sealed class FakeSource : IAppUpdateSource
    {
        public bool IsInstalled { get; set; } = true;
        public string CurrentVersion { get; set; } = "0.1.0-preview.4";
        public string? PendingVersion { get; set; }
        public string? Offer { get; set; }
        public Exception? FindError { get; set; }
        public TaskCompletionSource? DownloadGate { get; set; }
        public int Finds, Downloads, Applies;

        public Task<string?> FindUpdateAsync(CancellationToken cancellationToken)
        {
            Finds++;
            if (FindError != null) throw FindError;
            return Task.FromResult(Offer);
        }

        public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            progress(40);
            if (DownloadGate != null) await DownloadGate.Task;
            progress(100);
        }

        public void ApplyAfterExit() => Applies++;
    }

    [Fact]
    public void PortableCopyCannotUpdate()
    {
        var source = new FakeSource { IsInstalled = false, Offer = "0.2.0" };
        using var updates = new AppUpdateService(source, () => true);
        Assert.Equal(AppUpdateState.Unavailable, updates.State);
        Assert.False(updates.CanCheck);
        Assert.Contains("doesn’t update itself", updates.StatusText);
        Assert.False(updates.RestartToUpdate());
        Assert.Equal(0, source.Applies);
    }

    [Fact]
    public async Task UpToDateWhenNoNewerVersion()
    {
        var source = new FakeSource();
        using var updates = new AppUpdateService(source, () => true);
        Assert.Equal("Version 0.1.0-preview.4", updates.StatusText);
        await updates.CheckAsync();
        Assert.Equal(AppUpdateState.UpToDate, updates.State);
        Assert.Equal(0, source.Downloads);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task DownloadsThenOffersRestart()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeSource { Offer = "0.1.0-preview.5", DownloadGate = gate };
        using var updates = new AppUpdateService(source, () => true);
        Task check = updates.CheckAsync();
        Assert.Equal(AppUpdateState.Downloading, updates.State);
        Assert.Equal(40, updates.Progress);
        Assert.Equal("Downloading version 0.1.0-preview.5… 40%", updates.StatusText);
        Assert.False(updates.CanCheck);
        await updates.CheckAsync();  // A second check while downloading is ignored.
        Assert.Equal(1, source.Finds);
        gate.SetResult();
        await check;
        Assert.True(updates.IsReady);
        Assert.StartsWith("Version 0.1.0-preview.5 is ready.", updates.StatusText);
        Assert.True(updates.RestartToUpdate());
        Assert.Equal(1, source.Applies);
    }

    [Fact]
    public void AlreadyDownloadedUpdateIsReadyAtLaunch()
    {
        var source = new FakeSource { PendingVersion = "0.1.0-preview.5" };
        using var updates = new AppUpdateService(source, () => true);
        Assert.True(updates.IsReady);
        Assert.True(updates.RestartToUpdate());
    }

    [Fact]
    public async Task FailureIsReportedAndRetryable()
    {
        var source = new FakeSource { FindError = new HttpRequestException("offline") };
        using var updates = new AppUpdateService(source, () => true);
        await updates.CheckAsync();
        Assert.Equal(AppUpdateState.Failed, updates.State);
        Assert.Contains("Couldn’t check for updates", updates.StatusText);
        source.FindError = null;
        await updates.CheckAsync();
        Assert.Equal(AppUpdateState.UpToDate, updates.State);
    }

    [Fact]
    public async Task BackgroundChecksRespectTheSetting()
    {
        var source = new FakeSource();
        bool automatic = false;
        using var updates = new AppUpdateService(source, () => automatic);
        updates.Start(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        Assert.Equal(0, source.Finds);
        automatic = true;
        for (int i = 0; i < 100 && source.Finds == 0; i++) await Task.Delay(20);
        Assert.True(source.Finds > 0);
    }

    [Fact]
    public async Task DisposeStopsBackgroundChecks()
    {
        var source = new FakeSource();
        var updates = new AppUpdateService(source, () => true);
        updates.Start(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        updates.Dispose();
        await Task.Delay(400);
        Assert.Equal(0, source.Finds);
    }
}
