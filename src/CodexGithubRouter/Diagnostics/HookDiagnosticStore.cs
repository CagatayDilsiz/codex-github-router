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
        try
        {
            var path = GetFilePath(gitCommonDirectory, diagnosticEvent.InvocationId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temporaryPath = path + ".tmp";
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
            foreach (var file in Directory.EnumerateFiles(directory, "invocation-*.json"))
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
        catch (Exception)
        {
            // Best-effort diagnostics must never change hook behavior or exit codes.
        }
    }

    private static string FormatFileName(Guid invocationId) => $"invocation-{invocationId:N}.json";
}
