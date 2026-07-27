using System.Text;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Hooks;
using CodexGithubRouter.Work;

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
    "issue" => await IssuesCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "work" => await WorkCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "init" => await ConfigurationInitializer.InitAsync(args.Skip(1).ToArray()),
    "pull-request" or "pr" => await PullRequestCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    _ => UnknownCommand(args[0])
};

static int PrintVersion()
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var infoVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";


    Console.WriteLine("v" + VersionFormatter.Normalize(infoVersion));
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
          cgr issue <list|transition> <options> [working-directory]
          cgr work <status|reconcile|release> [working-directory]
          cgr init [--force]
          cgr pull-request|pr <list|transition> <options> [working-directory]

        Commands:
          hook        Run the hook service to process incoming codex payloads.
          auto        Manage autonomous mode for the repository.
          issue       Manage issues in the repository.
          work        Inspect, reconcile, or explicitly release repository work claims.
          init        Initialize the configuration for the Codex Github Router.
          pull-request|pr  Manage pull requests in the repository.
        """);

    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    return 2;
}

