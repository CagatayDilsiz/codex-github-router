using System.Text.Json;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Configurations;

public static class WorkflowConfigurationService
{
    public static async Task<RouterConfiguration> LoadOrCreateAsync(CancellationToken cancellationToken = default)
        => await LoadOrCreateAsync(ConfigurationPaths.Default, cancellationToken);

    public static async Task<RouterConfiguration> LoadOrCreateAsync(ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var path = paths.WorkflowFile;

        if (!File.Exists(path))
        {
            await WriteDefaultAsync(path, cancellationToken);
        }

        return await LoadAsync(path, cancellationToken);
    }

    public static async Task<RouterConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);

            var configuration = await JsonSerializer.DeserializeAsync<RouterConfiguration>(stream, WorkflowJson.Options, cancellationToken);

            if (configuration is null)
            {
                throw new InvalidOperationException("Workflow configuration is empty.");
            }

            Validate(configuration);

            return configuration;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Workflow configuration is not valid JSON: {path}", exception);
        }
    }

    public static async Task WriteDefaultAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
        ?? throw new InvalidOperationException("Configuration directory could not be resolved.");

        Directory.CreateDirectory(directory);       

        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            await JsonSerializer.SerializeAsync(stream, new RouterConfiguration(), WorkflowJson.Options, cancellationToken);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created it first. Ignore the exception and proceed to load the existing configuration.
        }
    }

    private static void Validate(RouterConfiguration configuration)
    {
        if (configuration.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported workflow configuration version: " +
                $"{configuration.Version}");
        }
       
        foreach (var state in Enum.GetValues<WorkflowState>())
        {
            if (!configuration.States.TryGetValue(state, out var rules) || rules.Count == 0)
            {
                throw new InvalidOperationException($"The {state} workflow state must contain at least one rule.");
            }
        }

        foreach (var state in Enum.GetValues<PullRequestState>())
        {
            if (!configuration.PullRequestStates.TryGetValue(state, out var rules) || rules.Count == 0)
            {
                throw new InvalidOperationException($"The {state} pull request state must contain at least one rule.");
            }
        }

        if (configuration.DefaultIssueSelection.Limit <= 0)
        {
            throw new InvalidOperationException("Issue selection limit must be greater than zero.");
        }

        WorkflowLabelConfiguration.ValidateNoConflictingLabels(configuration);
    }
}
