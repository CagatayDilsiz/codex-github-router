using System.Diagnostics;

namespace CodexGithubRouter.Helpers;

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string workingDirectory, string fileName, IEnumerable<string>? arguments, CancellationToken cancellationToken = default) =>
        await RunAsync(workingDirectory, fileName, arguments, environment: null, cancellationToken);

    public static async Task<ProcessResult> RunAsync(string workingDirectory, string fileName, IEnumerable<string>? arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken = default)
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

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

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

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch
            {
                // Ignore exceptions from killing the process
            }

            throw;
        }
      
        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = await outputTask,
            Error = await errorTask
        };
    }
}