using System.Text;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Github;
using CodexGithubRouter.Hooks;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

return args[0].ToLowerInvariant() switch
{
    "--version" or "-v" => PrintVersion(),
    "--help" or "-h" => PrintHelp(), 
    "hook" => await HookService.RunAsync(),
    "auto" => await AutonomousCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "issues" => await IssuesCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    _ => UnknownCommand(args[0])
};

static int PrintVersion()
{
    Console.WriteLine("0.0.1");
    return 0;
}

static int PrintHelp()
{ 
    Console.WriteLine(
        """
        Codex Github Router

        Usage:        
          cgr --version
          cgr --help
          cgr hook
          cgr auto <on|off|status> [working-directory]
          cgr issues [working-directory]

        Commands:
          hook        Run the hook service to process incoming codex payloads.
          auto        Manage autonomous mode for the repository.
          issues      List open issues in the repository.
        """);

    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return 2;
}

