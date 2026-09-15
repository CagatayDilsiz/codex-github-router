using System.Diagnostics;
using CodexGithubRouter.Daemon;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Integration")]
public sealed class CodexSessionHostTests
{
    [Fact]
    public async Task LaunchAsync_StartsProcess_RecordsIdentity_AndForwardsModelBeforePrompt()
    {
        using var sandbox = new TestSandbox();
        var capture = CreateCaptureScript(sandbox, sleepSeconds: 30);
        var host = new CodexSessionHost();
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            IssueNumber = 12,
            WorkType = WorkClaimType.Implementation,
            OwnerSessionId = "daemon-session"
        };
        var policy = new DaemonPolicy
        {
            Command = capture.Command,
            Args = new List<string> { "exec", "--skip-git-repo-check" },
            Model = "codex",
            IntervalSeconds = 60,
            FailureThreshold = 5
        };

        var session = await host.LaunchAsync(
            sandbox.RepositoryDirectory, claim, "issue #12", WorkflowItemType.NewIssue, "PROMPT_MARKER", policy, "daemon-session", CancellationToken.None);

        try
        {
            Assert.NotNull(session.ProcessId);
            Assert.NotNull(session.ProcessStartTimeUtc);
            Assert.Equal("codex", session.Model);
            Assert.True(await host.IsAliveAsync(session, CancellationToken.None));

            var capturedArguments = await ReadCapturedArgumentsAsync(capture, session.ProcessId!.Value);
            AssertArgumentOrder(capturedArguments, capture.IsShellQuoteStyle);
        }
        finally
        {
            await host.StopAsync(session, CancellationToken.None);
            await WaitUntilExitedAsync(host, session);
        }
    }

    [Fact]
    public async Task IsAliveAsync_ReturnsFalse_AfterTheProcessExitsNaturally()
    {
        using var sandbox = new TestSandbox();
        var capture = CreateCaptureScript(sandbox, sleepSeconds: 0);
        var host = new CodexSessionHost();
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            IssueNumber = 7,
            WorkType = WorkClaimType.Implementation,
            OwnerSessionId = "daemon-session"
        };
        var policy = new DaemonPolicy { Command = capture.Command, IntervalSeconds = 60, FailureThreshold = 5 };

        var session = await host.LaunchAsync(
            sandbox.RepositoryDirectory, claim, "issue #7", WorkflowItemType.NewIssue, "PROMPT_MARKER", policy, "daemon-session", CancellationToken.None);

        Assert.NotNull(session.ProcessId);
        await WaitUntilExitedAsync(host, session);
        Assert.False(await host.IsAliveAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_IsSafe_WhenTheProcessAlreadyExited()
    {
        using var sandbox = new TestSandbox();
        var capture = CreateCaptureScript(sandbox, sleepSeconds: 0);
        var host = new CodexSessionHost();
        var claim = new WorkClaim
        {
            ClaimId = Guid.NewGuid(),
            WorktreeId = WorkClaimStore.MainWorktreeIdentity,
            IssueNumber = 9,
            WorkType = WorkClaimType.Implementation,
            OwnerSessionId = "daemon-session"
        };
        var policy = new DaemonPolicy { Command = capture.Command, IntervalSeconds = 60, FailureThreshold = 5 };

        var session = await host.LaunchAsync(
            sandbox.RepositoryDirectory, claim, "issue #9", WorkflowItemType.NewIssue, "PROMPT_MARKER", policy, "daemon-session", CancellationToken.None);

        await WaitUntilExitedAsync(host, session);

        await host.StopAsync(session, CancellationToken.None);
        Assert.False(await host.IsAliveAsync(session, CancellationToken.None));
    }

    private static async Task WaitUntilExitedAsync(CodexSessionHost host, ActiveDaemonSession session)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (await host.IsAliveAsync(session, CancellationToken.None))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail("The session process did not exit within the expected window.");
            }

            await Task.Delay(50);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadCapturedArgumentsAsync(CaptureScript capture, int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(capture.ArgsFile))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail("The session did not write its captured arguments before the timeout.");
            }

            if (TryGetExitCode(processId, out _))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.True(File.Exists(capture.ArgsFile), "The session captured no arguments.");
        var content = await File.ReadAllTextAsync(capture.ArgsFile);
        return capture.IsShellQuoteStyle
            ? content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void AssertArgumentOrder(IReadOnlyList<string> arguments, bool lineBased)
    {
        // The configured daemon args come first, then the propagated model marker and its value,
        // then the generated work prompt — the --model must always precede the prompt so the CLI
        // parses it as an option and never as part of the prompt text.
        if (lineBased)
        {
            Assert.Equal(new[] { "exec", "--skip-git-repo-check", "--model", "codex", "PROMPT_MARKER" }, arguments);
        }
        else
        {
            var joined = string.Join(" ", arguments);
            Assert.Contains("--model codex PROMPT_MARKER", joined, StringComparison.Ordinal);
            Assert.DoesNotMatch("PROMPT_MARKER.*--model", joined);
        }
    }

    private static CaptureScript CreateCaptureScript(TestSandbox sandbox, int sleepSeconds)
    {
        var directory = Path.Combine(sandbox.Root, "session-host");
        Directory.CreateDirectory(directory);
        var argsFile = Path.Combine(directory, "args.txt");

        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, "capture.cmd");
            File.WriteAllText(batch, $"""
                @echo off
                echo %* > "{argsFile}"
                timeout /t {sleepSeconds} /nobreak > nul
                """);
            return new CaptureScript(batch, argsFile, IsShellQuoteStyle: false);
        }

        var shell = Path.Combine(directory, "capture.sh");
        var quotedArgsFile = argsFile.Replace("'", "'\\''");
        File.WriteAllText(shell, $"""
            #!/bin/sh
            printf '%s\n' "$@" > '{quotedArgsFile}'
            sleep {sleepSeconds}
            """);
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return new CaptureScript(shell, argsFile, IsShellQuoteStyle: true);
    }

    private static bool TryGetExitCode(int processId, out int exitCode)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            exitCode = process.HasExited ? process.ExitCode : 0;
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            exitCode = 0;
            return true;
        }
        catch (InvalidOperationException)
        {
            exitCode = 0;
            return true;
        }
    }

    private sealed record CaptureScript(string Command, string ArgsFile, bool IsShellQuoteStyle);
}