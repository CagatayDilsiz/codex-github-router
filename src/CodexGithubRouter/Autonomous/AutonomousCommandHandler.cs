namespace CodexGithubRouter.Autonomous;

public static class AutonomousCommandHandler
{
    public static async Task<int> HandleAsync(string[] args)
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
                    var enableResult = await AutonomousService.EnableAutonomousAsync(workingDirectory);
                    var configurationChangeMessage = enableResult.ConfigurationChanged ? " Workflow configuration changed and missing labels were provisioned." : string.Empty;
                    Console.WriteLine($"Autonomous mode enabled for {workingDirectory}. Created {enableResult.CreatedLabelCount} missing workflow label(s).{configurationChangeMessage}");
                    return 0;

                case "off":
                    await AutonomousService.DisableAutonomousAsync(workingDirectory);
                    Console.WriteLine($"Autonomous mode disabled for {workingDirectory}");
                    return 0;

                case "status":
                    var enabled = await AutonomousService.GetAutonomousStatusAsync(workingDirectory);
                    Console.WriteLine($"Autonomous mode is {(enabled ? "enabled" : "disabled")} for {workingDirectory}");
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
}
