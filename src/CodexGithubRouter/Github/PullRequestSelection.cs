namespace CodexGithubRouter.GitHub;

public sealed class PullRequestSelection
{
    public bool Id { get; init; } = true;
    public bool Number { get; init; } = true;

    public bool Title { get; init; } = true;
    public bool Body { get; init; } = false;
    public bool State { get; init; } = true;

    public bool Labels { get; init; } = false;
    public bool Comments { get; init; } = false;
    public bool ClosingIssuesReferences { get; init; } = false;

    public bool CreatedAt { get; init; } = false;
    public bool UpdatedAt { get; init; } = false;

    public string ToSelectionString()
    {
        var selectedFields = new List<string>();

        if (Id) selectedFields.Add("id");
        if (CreatedAt) selectedFields.Add("createdAt");
        if (Number) selectedFields.Add("number");
        if (State) selectedFields.Add("state");
        if (Labels) selectedFields.Add("labels");
        if (Comments) selectedFields.Add("comments");
        if (ClosingIssuesReferences) selectedFields.Add("closingIssuesReferences");
        if (Title) selectedFields.Add("title");
        if (Body) selectedFields.Add("body");
        if (UpdatedAt) selectedFields.Add("updatedAt");

        return string.Join(',', selectedFields);
    }

    public static PullRequestSelection SelectionWithAllFields()
    {
        return new PullRequestSelection
        {
            Id = true,
            Number = true,
            Title = true,
            Body = true,
            State = true,
            Labels = true,
            Comments = true,
            ClosingIssuesReferences = true,
            CreatedAt = true,
            UpdatedAt = true
        };
    }
}