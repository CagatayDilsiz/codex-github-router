using CodexGithubRouter.Configurations;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Autonomous;

public static class AutonomousCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new AutonomousCommandDependencies());

    public static async Task<int> HandleAsync(string[] args, AutonomousCommandDependencies dependencies)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cgr auto <on|off|status> <working-directory>");
            return 1;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var workingDirectory = args.Length > 1 ? args[1] : Environment.CurrentDirectory;

            switch (command)
            {
                case "on":
                    var enableResult = await dependencies.EnableAsync(workingDirectory);
                    var configurationChangeMessage = enableResult.ConfigurationChanged ? " Workflow configuration changed and missing labels were provisioned." : string.Empty;
                    dependencies.Output.WriteLine($"Autonomous mode enabled for {workingDirectory}. Created {enableResult.CreatedLabelCount} missing workflow label(s).{configurationChangeMessage}");
                    return 0;

                case "off":
                    await dependencies.DisableAsync(workingDirectory);
                    dependencies.Output.WriteLine($"Autonomous mode disabled for {workingDirectory}");
                    return 0;

                case "status":
                    var enabled = await dependencies.GetStatusAsync(workingDirectory);
                    var configuration = await dependencies.LoadConfigurationAsync(workingDirectory);
                    dependencies.Output.WriteLine(FormatStatus(enabled, configuration.Policies.AutonomousActivation));
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown command: {command}");
                    return 2;
            }           
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }



    }

    public static string FormatStatus(bool enabled, AutonomousActivationPolicy? activation)
    {
        var mode = activation?.Mode?.Trim() ?? "always";
        var lines = new List<string>
        {
            $"Autonomous mode: {(enabled ? "enabled" : "disabled")}",
            $"Activation mode: {mode.ToLowerInvariant()}"
        };

        if (string.Equals(mode, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (enabled)
            {
                lines.Add("Activation prompts:");
                lines.AddRange((activation?.Prompts ?? new List<string>()).Select(prompt => $"  - {prompt}"));
            }
            else
            {
                lines.Add($"Activation prompts: {activation?.Prompts?.Count ?? 0} configured");
            }
        }
        else
        {
            lines.Add("Activation prompts: ignored");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class AutonomousCommandDependencies
{
    public Func<string, Task<AutonomousEnableResult>> EnableAsync { get; init; } = workingDirectory => AutonomousService.EnableAutonomousAsync(workingDirectory);

    public Func<string, Task> DisableAsync { get; init; } = workingDirectory => AutonomousService.DisableAutonomousAsync(workingDirectory);

    public Func<string, Task<bool>> GetStatusAsync { get; init; } = workingDirectory => AutonomousService.GetAutonomousStatusAsync(workingDirectory);

    public Func<string, Task<RouterConfiguration>> LoadConfigurationAsync { get; init; } = workingDirectory => WorkflowConfigurationService.LoadEffectiveOrDefaultAsync(workingDirectory);

    public TextWriter Output { get; init; } = Console.Out;
}
