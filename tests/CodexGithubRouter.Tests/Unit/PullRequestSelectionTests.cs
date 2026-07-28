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
}
