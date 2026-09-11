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
    public bool HeadRefName { get; init; } = false;
    public bool UpdatedAt { get; init; } = false;

    public bool IsDraft { get; init; } = false;
    public bool Author { get; init; } = false;
    public bool ReviewRequests { get; init; } = false;
    public bool Reviews { get; init; } = false;

    public bool StatusCheckRollup { get; init; } = false;
    public bool Mergeable { get; init; } = false;
    public bool ReviewDecision { get; init; } = false;

    public string ToSelectionString()
    {
        var selectedFields = new List<string>();

        if (Id) selectedFields.Add("id");
        if (CreatedAt) selectedFields.Add("createdAt");
        if (HeadRefName) selectedFields.Add("headRefName");
        if (Number) selectedFields.Add("number");
        if (State) selectedFields.Add("state");
        if (Labels) selectedFields.Add("labels");
        if (Comments) selectedFields.Add("comments");
        if (ClosingIssuesReferences) selectedFields.Add("closingIssuesReferences");
        if (Title) selectedFields.Add("title");
        if (Body) selectedFields.Add("body");
        if (UpdatedAt) selectedFields.Add("updatedAt");
        if (IsDraft) selectedFields.Add("isDraft");
        if (Author) selectedFields.Add("author");
        if (ReviewRequests) selectedFields.Add("reviewRequests");
        if (Reviews) selectedFields.Add("reviews");
        if (StatusCheckRollup) selectedFields.Add("statusCheckRollup");
        if (Mergeable) selectedFields.Add("mergeable");
        if (ReviewDecision) selectedFields.Add("reviewDecision");

        return string.Join(',', selectedFields);
    }

    /// <summary>
    /// A copy of this selection augmented with the GitHub-native signals (status check rollup,
    /// mergeability and review decision). Native signal evaluation is opt-in through
    /// <c>policies.nativeSignals</c>, so production callers apply this only when that policy is
    /// enabled — keeping the default fetch small and rate-limit friendly.
    /// </summary>
    public PullRequestSelection WithNativeSignals() => new()
    {
        Id = Id,
        Number = Number,
        Title = Title,
        Body = Body,
        State = State,
        Labels = Labels,
        Comments = Comments,
        ClosingIssuesReferences = ClosingIssuesReferences,
        CreatedAt = CreatedAt,
        HeadRefName = HeadRefName,
        UpdatedAt = UpdatedAt,
        IsDraft = IsDraft,
        Author = Author,
        ReviewRequests = ReviewRequests,
        Reviews = Reviews,
        StatusCheckRollup = true,
        Mergeable = true,
        ReviewDecision = true
    };

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
            HeadRefName = true,
            UpdatedAt = true,
            IsDraft = true,
            Author = true,
            ReviewRequests = true,
            Reviews = true,
            StatusCheckRollup = true,
            Mergeable = true,
            ReviewDecision = true
        };
    }

    public static PullRequestSelection ReviewSelection() => new()
    {
        Number = true,
        State = true,
        Labels = true,
        Title = true,
        IsDraft = true,
        Author = true,
        ReviewRequests = true,
        Reviews = true
    };
}
