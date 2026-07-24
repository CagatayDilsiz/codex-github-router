namespace CodexGithubRouter.Autonomous;

public static class AutonomousCommandHandler
{
    public static async Task<int> HandleAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: cgr auto <on|off|status> <working-directory>");
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var workingDirectory = args[1];

        switch (command)
        {
            case "on":
                await AutonomousService.EnableAutonomousAsync(workingDirectory);
                Console.WriteLine($"Autonomous mode enabled for {workingDirectory}");
                break;

            case "off":
                await AutonomousService.DisableAutonomousAsync(workingDirectory);
                Console.WriteLine($"Autonomous mode disabled for {workingDirectory}");
                break;

            case "status":
                var isAutonomous = await AutonomousService.IsAutonomousAsync(workingDirectory);
                Console.WriteLine($"Autonomous mode is {(isAutonomous ? "enabled" : "disabled")} for {workingDirectory}");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                return 1;
        }

        return 0;
    }
}