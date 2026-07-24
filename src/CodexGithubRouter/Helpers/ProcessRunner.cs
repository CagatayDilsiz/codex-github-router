using System.Diagnostics;

namespace CodexGithubRouter.Helpers;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string workingDirectory, string fileName, IEnumerable<string>? arguments,  CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask, waitForExitTask);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = outputTask.Result,
            Error = errorTask.Result
        };
    }
}