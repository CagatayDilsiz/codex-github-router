namespace CodexGithubRouter.Doctor;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Failure
}

public sealed class DoctorCheck
{
    public required string Name { get; init; }

    public required DoctorCheckStatus Status { get; init; }

    public string? Detail { get; init; }
}

public sealed class DoctorResult
{
    public string WorkingDirectory { get; init; } = string.Empty;

    public string? RepositoryRoot { get; set; }

    public List<DoctorCheck> Checks { get; } = new();

    public bool HasFailure => Checks.Any(check => check.Status == DoctorCheckStatus.Failure);

    public int PassCount => Checks.Count(check => check.Status == DoctorCheckStatus.Pass);

    public int WarningCount => Checks.Count(check => check.Status == DoctorCheckStatus.Warning);

    public int FailureCount => Checks.Count(check => check.Status == DoctorCheckStatus.Failure);
}
