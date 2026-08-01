using System.Text.Json;

namespace CodexGithubRouter.Diagnostics;

public static class HookDiagnosticStore
{
    public const string DiagnosticsDirectoryName = "codex-github-router.diagnostics";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetDirectoryPath(string gitCommonDirectory) =>
        Path.Combine(gitCommonDirectory, DiagnosticsDirectoryName);

    public static string GetFilePath(string gitCommonDirectory, Guid invocationId) =>
        Path.Combine(GetDirectoryPath(gitCommonDirectory), FormatFileName(invocationId));

    public static Task WriteAsync(string gitCommonDirectory, HookDiagnosticEvent diagnosticEvent)
        => WriteAsync(gitCommonDirectory, diagnosticEvent, CancellationToken.None);

    public static async Task WriteAsync(string gitCommonDirectory, HookDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken)
    {
        var temporaryPath = string.Empty;
        try
        {
            var path = GetFilePath(gitCommonDirectory, diagnosticEvent.InvocationId);
            temporaryPath = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, diagnosticEvent, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        catch (Exception)
        {
            // Best-effort diagnostics must never change hook behavior or exit codes.
        }
        finally
        {
            if (temporaryPath.Length > 0 && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception)
                {
                    // Best-effort cleanup must never change hook behavior or exit codes.
                }
            }
        }
    }

    public static Task PruneAsync(string gitCommonDirectory, int retentionDays)
        => PruneAsync(gitCommonDirectory, retentionDays, CancellationToken.None);

    public static async Task PruneAsync(string gitCommonDirectory, int retentionDays, CancellationToken cancellationToken)
    {
        try
        {
            if (retentionDays < 1)
            {
                return;
            }

            var directory = GetDirectoryPath(gitCommonDirectory);
            if (!Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (var pattern in new[] { "invocation-*.json", "invocation-*.json.tmp" })
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostics must never change hook behavior or exit codes.
        }
    }

    private static string FormatFileName(Guid invocationId) => $"invocation-{invocationId:N}.json";
}
