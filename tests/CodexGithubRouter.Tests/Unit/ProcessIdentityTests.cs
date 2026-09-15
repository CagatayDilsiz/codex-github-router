using CodexGithubRouter.Daemon;
using Xunit;

namespace CodexGithubRouter.Tests.Unit;

public class ProcessIdentityTests
{
    [Fact]
    public void IsAlive_True_ForLiveProcess_WithMatchingStartTime()
    {
        var startTime = ProcessIdentity.GetCurrentProcessStartTimeUtc();

        Assert.True(ProcessIdentity.IsAlive(Environment.ProcessId, startTime));
    }

    [Fact]
    public void IsAlive_True_ForLiveProcess_WhenExpectedStartTimeIsMissing()
    {
        // Legacy state without a recorded start time still requires a live process.
        Assert.True(ProcessIdentity.IsAlive(Environment.ProcessId, null));
    }

    [Fact]
    public void IsAlive_False_WhenStartTimeMismatch_RejectsRecycledPid()
    {
        var startTime = ProcessIdentity.GetCurrentProcessStartTimeUtc();
        var wrongStartTime = startTime + TimeSpan.FromHours(1);

        // A live PID whose start time does not match the recorded identity must be treated as a
        // different process (the PID was recycled), never as the daemon we record as running.
        Assert.False(ProcessIdentity.IsAlive(Environment.ProcessId, wrongStartTime));
    }

    [Fact]
    public void IsAlive_False_ForUnexistingPid()
    {
        Assert.False(ProcessIdentity.IsAlive(int.MaxValue, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryGetStartTime_ReturnsTrue_ForCurrentProcess()
    {
        Assert.True(ProcessIdentity.TryGetStartTimeUtc(Environment.ProcessId, out var startTime));

        // The test-runner process was started in the recent past; the exact age is
        // indeterminate under parallel execution so we only assert a reasonable window.
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        Assert.True(startTime >= oneHourAgo);
        Assert.True(startTime <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryGetStartTime_ReturnsFalse_ForUnexistingPid()
    {
        Assert.False(ProcessIdentity.TryGetStartTimeUtc(int.MaxValue, out var startTime));
        Assert.Equal(default, startTime);
    }
}