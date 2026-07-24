using System.Diagnostics;
namespace CodexGithubRouter.Git;

public static class GitRepositoryService
{
    public static async Task<string?> GetCommonDirectoryAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("--git-common-dir");

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask, waitForExitTask);

        if (process.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(
                $"Git command failed with exit code {process.ExitCode}: {errorTask.Result}");

            return null;
        }

        var output = outputTask.Result.Trim();

        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Error.WriteLineAsync(
                "Git command returned empty output for common directory.");

            return null;
        }

        if (Path.IsPathRooted(output))
        {
            return Path.GetFullPath(output);
        }

        return Path.GetFullPath(Path.Combine(workingDirectory, output));
    }
}