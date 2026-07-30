using System.Text.Json;
using CodexGithubRouter.Git;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Configurations;

public static class ConfigCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new ConfigCommandDependencies());

    public static async Task<int> HandleAsync(string[] args, ConfigCommandDependencies dependencies)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cgr config <path|show|validate> [options]");
            return 2;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var subArgs = args.Skip(1).ToArray();

            return command switch
            {
                "path" => await ShowPathsAsync(subArgs, dependencies),
                "show" => await ShowConfigAsync(subArgs, dependencies),
                "validate" => await ValidateConfigAsync(subArgs, dependencies),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ShowPathsAsync(string[] args, ConfigCommandDependencies dependencies)
    {
        if (args.Length > 1)
        {
            return UsageError("path", "too many arguments");
        }

        if (args.Length == 1 && IsOptionArgument(args[0]))
        {
            return UsageError("path", $"unknown option: {args[0]}");
        }

        dependencies.Output.WriteLine(ConfigurationPaths.Default.WorkflowFile);

        var workingDirectory = args.Length == 1 ? args[0] : Environment.CurrentDirectory;
        var repositoryRoot = await dependencies.GetRepositoryRootAsync(workingDirectory);
        if (repositoryRoot is not null)
        {
            var repoConfigPath = Path.Combine(repositoryRoot, ".codex-github-router", "workflow.json");
            if (dependencies.FileExists(repoConfigPath))
            {
                dependencies.Output.WriteLine(repoConfigPath);
            }
        }

        return 0;
    }

    private static async Task<int> ShowConfigAsync(string[] args, ConfigCommandDependencies dependencies)
    {
        var isEffective = args.Length > 0 &&
            string.Equals(args[0], "--effective", StringComparison.OrdinalIgnoreCase);

        if (!isEffective && args.Length > 0)
        {
            return UsageError("show", $"unknown option: {args[0]}");
        }

        if (isEffective && args.Length > 2)
        {
            return UsageError("show", "too many arguments");
        }

        if (isEffective && args.Length == 2 && IsOptionArgument(args[1]))
        {
            return UsageError("show", $"unknown option: {args[1]}");
        }

        RouterConfiguration config;

        if (isEffective)
        {
            var workingDirectory = args.Length == 2 ? args[1] : Environment.CurrentDirectory;
            config = await dependencies.LoadEffectiveAsync(workingDirectory);
        }
        else
        {
            config = await dependencies.LoadGlobalAsync();
        }

        var json = JsonSerializer.Serialize(config, WorkflowJson.Options);
        dependencies.Output.WriteLine(json);
        return 0;
    }

    private static async Task<int> ValidateConfigAsync(string[] args, ConfigCommandDependencies dependencies)
    {
        if (args.Length > 1)
        {
            return UsageError("validate", "too many arguments");
        }

        if (args.Length == 1 && IsOptionArgument(args[0]))
        {
            return UsageError("validate", $"unknown option: {args[0]}");
        }

        var workingDirectory = args.Length == 1 ? args[0] : Environment.CurrentDirectory;

        try
        {
            _ = await dependencies.LoadEffectiveAsync(workingDirectory);
            dependencies.Output.WriteLine("Configuration is valid.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            dependencies.Output.WriteLine($"Configuration is invalid: {ex.Message}");
            return 1;
        }
    }

    private static bool IsOptionArgument(string value) =>
        value.StartsWith("--", StringComparison.Ordinal);

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown config subcommand: {command}");
        return 2;
    }

    private static int UsageError(string subcommand, string detail)
    {
        Console.Error.WriteLine($"cgr config {subcommand}: {detail}");
        return 2;
    }
}

public sealed class ConfigCommandDependencies
{
    public Func<Task<RouterConfiguration>> LoadGlobalAsync { get; init; }
        = () => WorkflowConfigurationService.LoadOrDefaultAsync();

    public Func<string, Task<RouterConfiguration>> LoadEffectiveAsync { get; init; }
        = workingDirectory => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory);

    public Func<string, Task<string?>> GetRepositoryRootAsync { get; init; }
        = workingDirectory => GitRepositoryService.GetRepositoryRootAsync(workingDirectory);

    public Func<string, bool> FileExists { get; init; }
        = path => File.Exists(path);

    public TextWriter Output { get; init; } = Console.Out;
}
