using System.Diagnostics;
using CodexGithubRouter.GitWorks;
using CodexGithubRouter.Hooks;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousService
{
    private const string AutonomousFileName = "codex-github-router.auto";

    public static async Task<bool> IsAutonomousAsync(HookPayload payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.Cwd) || !Directory.Exists(payload.Cwd))
        {
            return false;
        }

        try
        {
            var gitCommonDirectory = await GitRepositoryService.GetCommonDirectoryAsync(payload.Cwd, cancellationToken);

            if (gitCommonDirectory is null)
            {
                return false;
            }

            var autonomousFilePath = Path.Combine(gitCommonDirectory, AutonomousFileName);

            return File.Exists(autonomousFilePath);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Autonomous mode kontrol edilemedi: {exception.Message}");

            return false;
        }
    }
}