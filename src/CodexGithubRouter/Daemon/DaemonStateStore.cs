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
                // Once a state file exists, empty/whitespace is invalid state: treating it as "no
                // state" would silently mint a fresh supervisor identity while the previous owner's
                // claims/processes may still exist.
                throw new DaemonStateFileException(statePath, new InvalidOperationException("The daemon state file exists but is empty."));
            }

            var state = JsonSerializer.Deserialize<DaemonExecutionState>(content, Options);
            if (state is null)
            {
                // A literal JSON 'null' is just as invalid as malformed JSON once the file exists.
                throw new DaemonStateFileException(statePath, new InvalidOperationException("The daemon state file contains a null state."));
            }

            return state;
        }
        catch (JsonException exception)
        {
            // Fail closed like the work-claim store: a corrupt daemon state file is never treated
            // as "no state", because that would let a fresh daemon silently claim work the previous
            // owner is still supervising (or let an operator's stop signal be ignored). The caller
            // surfaces recovery guidance instead of continuing.
            throw new DaemonStateFileException(statePath, exception);
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