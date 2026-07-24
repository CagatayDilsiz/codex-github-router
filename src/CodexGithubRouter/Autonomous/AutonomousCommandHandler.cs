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
                    await AutonomousService.EnableAutonomousAsync(workingDirectory);
                    Console.WriteLine($"Autonomous mode enabled for {workingDirectory}");
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