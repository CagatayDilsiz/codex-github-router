using CodexGithubRouter.Daemon;
using CodexGithubRouter.Work;
using Xunit;

namespace CodexGithubRouter.Tests.Unit;

public class DaemonStateStoreTests
{
    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenNoStateFileExists()
    {
        using var sandbox = new TestSandbox();

        Assert.Null(await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));
    }

    [Fact]
    public async Task WriteAndRead_RoundTripsActiveSessions()
    {
        using var sandbox = new TestSandbox();
        var state = new DaemonExecutionState
        {
            DaemonSessionId = "session-1",
            Pid = 1234,
            PidStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            StopRequested = false,
            StoppedAt = null,
            ActiveSessions = new Dictionary<string, ActiveDaemonSession>
            {
                [WorkClaimStore.MainWorktreeIdentity] = new ActiveDaemonSession
                {
                    ClaimId = Guid.NewGuid(),
                    WorkIdentity = "issue #12",
                    ProcessId = 4242,
                    ProcessStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                    Model = "codex",
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                }
            }
        };

        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, state);

        var read = await DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory);
        Assert.NotNull(read);
        Assert.Equal("session-1", read!.DaemonSessionId);
        Assert.Equal(1234, read.Pid);
        Assert.Equal(state.PidStartTimeUtc, read.PidStartTimeUtc);
        var session = read.ActiveSessions[WorkClaimStore.MainWorktreeIdentity];
        Assert.Equal(4242, session.ProcessId);
        Assert.Equal("codex", session.Model);
        Assert.Equal("issue #12", session.WorkIdentity);
    }

    [Fact]
    public async Task ReadAsync_FailsClosed_OnCorruptStateFile()
    {
        using var sandbox = new TestSandbox();
        var statePath = DaemonStateStore.GetStatePath(sandbox.GitCommonDirectory);
        Directory.CreateDirectory(sandbox.GitCommonDirectory);
        await File.WriteAllTextAsync(statePath, "{ this is not valid json ]");

        var exception = await Assert.ThrowsAsync<DaemonStateFileException>(
            () => DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));

        Assert.Equal(statePath, exception.StatePath);
        Assert.Contains("must be repaired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_FailsClosed_WhenJsonShapeIsInvalid()
    {
        using var sandbox = new TestSandbox();
        var statePath = DaemonStateStore.GetStatePath(sandbox.GitCommonDirectory);
        Directory.CreateDirectory(sandbox.GitCommonDirectory);
        await File.WriteAllTextAsync(statePath, """{"DaemonSessionId": 42, "ActiveSessions": "not-a-dictionary"}""");

        var exception = await Assert.ThrowsAsync<DaemonStateFileException>(
            () => DaemonStateStore.ReadAsync(sandbox.GitCommonDirectory));

        Assert.Equal(statePath, exception.StatePath);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheStateFile()
    {
        using var sandbox = new TestSandbox();
        Directory.CreateDirectory(sandbox.GitCommonDirectory);
        await DaemonStateStore.WriteAsync(sandbox.GitCommonDirectory, new DaemonExecutionState { DaemonSessionId = "s" });

        Assert.True(DaemonStateStore.Exists(sandbox.GitCommonDirectory));
        await DaemonStateStore.DeleteAsync(sandbox.GitCommonDirectory);
        Assert.False(DaemonStateStore.Exists(sandbox.GitCommonDirectory));
    }
}