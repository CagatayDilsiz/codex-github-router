namespace CodexGithubRouter.Autonomous;

public sealed class AutonomousState
{
    public string ConfigurationFingerprint { get; init; } = string.Empty;
}

public sealed class AutonomousEnableResult
{
    public int CreatedLabelCount { get; init; }
    public bool ConfigurationChanged { get; init; }
}
