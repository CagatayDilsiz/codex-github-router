using System.Text.Json;

namespace CodexGithubRouter.Daemon;

/// <summary>
/// Reads and writes the daemon execution state stored in a repository's Git common directory.
/// The state file is co-located with the work-claim file so a repository and its worktrees share a
/// single, atomic source of truth for the daemon supervisor.
/// </summary>
public static class DaemonStateStore
{
    public const string StateFileName = "codex-github-router.daemon.json";
    public const string ActiveSessionClaimIdKey = "activeSession.claimId";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetStatePath(string gitCommonDirectory) =>
        Path.Combine(gitCommonDirectory, StateFileName);

    public static Task<DaemonExecutionState?> ReadAsync(string gitCommonDirectory, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetStatePath(gitCommonDirectory), cancellationToken);

    private static async Task<DaemonExecutionState?> ReadFileAsync(string statePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(statePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            return JsonSerializer.Deserialize<DaemonExecutionState>(content, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task WriteAsync(string gitCommonDirectory, DaemonExecutionState state, CancellationToken cancellationToken = default)
        => WriteFileAsync(GetStatePath(gitCommonDirectory), state, cancellationToken);

    private static async Task WriteFileAsync(string statePath, DaemonExecutionState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = statePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, Options, cancellationToken);
        }

        File.Move(temporaryPath, statePath, overwrite: true);
    }

    public static Task DeleteAsync(string gitCommonDirectory, CancellationToken cancellationToken = default) =>
        DeleteFileAsync(GetStatePath(gitCommonDirectory), cancellationToken);

    private static Task DeleteFileAsync(string statePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return Task.CompletedTask;
        }

        File.Delete(statePath);
        return Task.CompletedTask;
    }

    public static bool Exists(string gitCommonDirectory) =>
        File.Exists(GetStatePath(gitCommonDirectory));
}