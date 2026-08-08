using System.Text.Json;
using System.Text.Json.Nodes;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Configurations;

public static class WorkflowConfigurationService
{
    private const string RepositoryConfigurationDirectoryName = ".codex-github-router";
    private const string RepositoryWorkflowFileName = "workflow.json";

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

    public static Task<RouterConfiguration> LoadOrDefaultAsync(CancellationToken cancellationToken = default)
        => LoadOrDefaultAsync(ConfigurationPaths.Default, cancellationToken);

    public static Task<RouterConfiguration> LoadOrDefaultAsync(ConfigurationPathSet paths, CancellationToken cancellationToken = default)
        => File.Exists(paths.WorkflowFile)
            ? LoadAsync(paths.WorkflowFile, cancellationToken)
            : Task.FromResult(new RouterConfiguration());

    public static Task<RouterConfiguration> LoadEffectiveAsync(string workingDirectory, CancellationToken cancellationToken = default)
        => LoadEffectiveAsync(workingDirectory, ConfigurationPaths.Default, cancellationToken);

    public static async Task<RouterConfiguration> LoadEffectiveAsync(string workingDirectory, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var repositoryRoot = await GitRepositoryService.GetRepositoryRootAsync(workingDirectory, cancellationToken)
            ?? throw new InvalidOperationException("Not a valid Git repository.");
        return await LoadEffectiveFromRepositoryRootAsync(repositoryRoot, paths, cancellationToken);
    }

    public static async Task<RouterConfiguration> LoadEffectiveFromRepositoryRootAsync(string repositoryRoot, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        var globalConfiguration = File.Exists(paths.WorkflowFile)
            ? await LoadAsync(paths.WorkflowFile, cancellationToken)
            : new RouterConfiguration();
        var globalJson = File.Exists(paths.WorkflowFile)
            ? ParseGlobalJson(paths)
            : JsonSerializer.SerializeToNode(globalConfiguration, WorkflowJson.Options)!;
        return await ApplyRepositoryOverrideAsync(repositoryRoot, globalConfiguration, globalJson, cancellationToken);
    }

    public static Task<RouterConfiguration> LoadEffectiveOrDefaultAsync(string workingDirectory, CancellationToken cancellationToken = default)
        => LoadEffectiveAsync(workingDirectory, cancellationToken);

    public static Task<RouterConfiguration> LoadEffectiveOrDefaultAsync(string workingDirectory, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
        => LoadEffectiveAsync(workingDirectory, paths, cancellationToken);

    public static Task<DiagnosticsPolicy?> TryResolveDiagnosticsPolicyAsync(CancellationToken cancellationToken = default)
        => TryResolveGlobalDiagnosticsPolicyAsync(ConfigurationPaths.Default, cancellationToken);

    public static Task<DiagnosticsPolicy?> TryResolveDiagnosticsPolicyAsync(string? workingDirectory, CancellationToken cancellationToken = default)
        => TryResolveDiagnosticsPolicyAsync(workingDirectory, ConfigurationPaths.Default, cancellationToken);

    public static async Task<DiagnosticsPolicy?> TryResolveDiagnosticsPolicyAsync(string? workingDirectory, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        try
        {
            var repositoryRoot = string.IsNullOrWhiteSpace(workingDirectory)
                ? null
                : await GitRepositoryService.GetRepositoryRootAsync(workingDirectory, cancellationToken);
            if (repositoryRoot is null)
            {
                return await TryResolveGlobalDiagnosticsPolicyAsync(paths, cancellationToken);
            }

            return await TryResolveDiagnosticsPolicyFromRepositoryRootAsync(repositoryRoot, paths, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<DiagnosticsPolicy?> TryResolveDiagnosticsPolicyFromRepositoryRootAsync(string repositoryRoot, ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = await LoadEffectiveFromRepositoryRootAsync(repositoryRoot, paths, cancellationToken);
            return configuration.Policies.Diagnostics;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<DiagnosticsPolicy?> TryResolveGlobalDiagnosticsPolicyAsync(ConfigurationPathSet paths, CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = await LoadOrDefaultAsync(paths, cancellationToken);
            return configuration.Policies.Diagnostics;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<RouterConfiguration> ApplyRepositoryOverrideAsync(
        string repositoryRoot,
        RouterConfiguration globalConfiguration,
        JsonNode globalJson,
        CancellationToken cancellationToken)
    {
        var repositoryWorkflowPath = Path.Combine(repositoryRoot, RepositoryConfigurationDirectoryName, RepositoryWorkflowFileName);

        if (!File.Exists(repositoryWorkflowPath))
        {
            return globalConfiguration;
        }

        JsonNode repositoryJson;
        try
        {
            repositoryJson = JsonNode.Parse(await File.ReadAllTextAsync(repositoryWorkflowPath, cancellationToken))
                ?? throw new InvalidOperationException($"Repository workflow configuration is empty or null: {repositoryWorkflowPath}");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Repository workflow configuration is not valid JSON: {repositoryWorkflowPath}", exception);
        }

        if (repositoryJson is not JsonObject)
        {
            throw new InvalidOperationException($"Repository workflow configuration must be a JSON object: {repositoryWorkflowPath}");
        }

        RejectExplicitNulls(repositoryJson, repositoryWorkflowPath);
        var mergedJson = MergeJson(globalJson, repositoryJson);

        try
        {
            var effectiveConfiguration = JsonSerializer.Deserialize<RouterConfiguration>(mergedJson.ToJsonString(WorkflowJson.Options), WorkflowJson.Options)
                ?? throw new InvalidOperationException("Effective workflow configuration is empty.");

            Validate(effectiveConfiguration);
            return effectiveConfiguration;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Effective workflow configuration is not valid after applying repository override '{repositoryWorkflowPath}'.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Effective workflow configuration is invalid after applying repository override '{repositoryWorkflowPath}': {exception.Message}", exception);
        }
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

    private static JsonNode MergeJson(JsonNode inherited, JsonNode overlay)
    {
        if (inherited is JsonObject inheritedObject && overlay is JsonObject overlayObject)
        {
            var result = inheritedObject.DeepClone().AsObject();
            foreach (var property in overlayObject)
            {
                var existingProperty = result.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, property.Key, StringComparison.OrdinalIgnoreCase));

                if (existingProperty.Key is not null)
                {
                    result[existingProperty.Key] = MergeJson(existingProperty.Value!, property.Value!);
                }
                else
                {
                    result[property.Key] = property.Value!.DeepClone();
                }
            }

            return result;
        }

        return overlay.DeepClone();
    }

    private static JsonNode ParseGlobalJson(ConfigurationPathSet paths)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(paths.WorkflowFile))
                ?? throw new InvalidOperationException("Global workflow configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Workflow configuration is not valid JSON: {paths.WorkflowFile}", exception);
        }
    }

    private static void RejectExplicitNulls(JsonNode node, string path)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                if (property.Value is null)
                {
                    throw new InvalidOperationException($"Explicit null values are not supported in repository workflow configuration: {path} (property '{property.Key}').");
                }

                RejectExplicitNulls(property.Value, path);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is null)
                {
                    throw new InvalidOperationException($"Explicit null values are not supported in repository workflow configuration: {path} (array index {index}).");
                }

                RejectExplicitNulls(jsonArray[index]!, path);
            }
        }
    }

    public static string? ValidateConfiguration(RouterConfiguration configuration)
    {
        try
        {
            Validate(configuration);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
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

        AutonomousActivationService.Validate(configuration.Policies.AutonomousActivation);
        WorkerRoutingService.Validate(configuration);
        WorkflowLabelConfiguration.ValidateNoConflictingLabels(configuration);

        if (configuration.Policies.Diagnostics.RetentionDays < 1)
        {
            throw new InvalidOperationException("Diagnostics retention days must be at least one.");
        }
    }
}
