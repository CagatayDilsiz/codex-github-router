namespace CodexGithubRouter.Work;

public sealed class WorkClaimFileException : Exception
{
    public WorkClaimFileException(string message) : base(message)
    {
    }

    public WorkClaimFileException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
