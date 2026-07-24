using System.Text;

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
        """);

    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Bilinmeyen komut: {command}");
    return 2;
}