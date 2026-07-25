using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Helpers;

public static class ConfigurationInitializer
{
    public static async Task<int> InitAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var force = args.Any(argument => string.Equals(argument, "--force", StringComparison.OrdinalIgnoreCase));

        var path = ConfigurationPaths.WorkflowFile;

        if (File.Exists(path) && !force)
        {
            Console.WriteLine($"Configuration already exists: {path}");

            return 0;
        }

        await WorkflowConfigurationService.WriteDefaultAsync(path, cancellationToken);

        Console.WriteLine($"Configuration written: {path}");

        return 0;
    }
}