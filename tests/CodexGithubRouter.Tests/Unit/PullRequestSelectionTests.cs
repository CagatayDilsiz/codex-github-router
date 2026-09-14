using CodexGithubRouter.GitHub;
using Xunit;

namespace CodexGithubRouter.Tests;

[Trait("Category", "Unit")]
public sealed class PullRequestSelectionTests
{
    [Fact]
    public void In_progress_projection_requests_closing_issue_references()
    {
        var selection = new PullRequestSelection
        {
            Number = true,
            State = true,
            Labels = true,
            ClosingIssuesReferences = true
        };

        Assert.Contains("closingIssuesReferences", selection.ToSelectionString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNativeSignals_always_fetches_state_and_is_draft()
    {
        var selection = new PullRequestSelection
        {
            Number = true,
            Labels = true,
            ClosingIssuesReferences = true
        };

        var nativeSelection = selection.WithNativeSignals();
        var selectionString = nativeSelection.ToSelectionString();

        Assert.True(nativeSelection.State);
        Assert.True(nativeSelection.IsDraft);
        Assert.Contains("state", selectionString, StringComparison.Ordinal);
        Assert.Contains("isDraft", selectionString, StringComparison.Ordinal);
        Assert.Contains("statusCheckRollup", selectionString, StringComparison.Ordinal);
        Assert.Contains("mergeable", selectionString, StringComparison.Ordinal);
        Assert.Contains("reviewDecision", selectionString, StringComparison.Ordinal);
    }
}
