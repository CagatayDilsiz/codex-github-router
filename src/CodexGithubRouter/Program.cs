using System.Text;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Doctor;
using CodexGithubRouter.Explain;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Configurations;
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
    "hook" => args.Length > 1 && string.Equals(args[1], "uninstall", StringComparison.OrdinalIgnoreCase)
        ? await ConfigurationInitializer.UninstallHookAsync(args.Skip(2).ToArray())
        : await HookService.RunAsync(),
    "auto" => await AutonomousCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "issue" => await IssuesCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "work" => await WorkCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "explain" => await ExplainCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "init" => await ConfigurationInitializer.InitAsync(args.Skip(1).ToArray()),
    "config" => await ConfigCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "doctor" => await DoctorCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    "pull-request" or "pr" => await PullRequestCommandHandler.HandleAsync(args.Skip(1).ToArray()),
    _ => UnknownCommand(args[0])
};

static int PrintVersion()
{
    Console.WriteLine("v" + VersionFormatter.GetVersion());
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
          cgr hook uninstall
          cgr auto <on|off|status> [working-directory]
          cgr issue <list|transition> <options> [working-directory]
          cgr work <status|list|reconcile|release> [working-directory]
          cgr explain [--issue <number>] [--model <model>] [working-directory]
          cgr config path [working-directory]
          cgr config show [--effective [working-directory]]
          cgr config validate [working-directory]
          cgr doctor [working-directory] [--model <model>]
          cgr init [--force]
          cgr pull-request|pr <list|transition> <options> [working-directory]

        Commands:
          hook        Run the hook service to process incoming codex payloads.
          hook uninstall  Remove CGR hook entries from the Codex hooks configuration.
          auto        Manage autonomous mode for the repository.
          issue       Manage issues in the repository.
          work        Inspect, reconcile, or explicitly release repository work claims.
          explain     Explain routing decisions for one or all workflow issues.
          config      Inspect and validate configuration (path, show, validate).
          doctor      Run read-only diagnostics for the environment and repository.
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

