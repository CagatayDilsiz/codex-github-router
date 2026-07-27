using System.Text.Json.Serialization;

namespace CodexGithubRouter.GitHub;

public sealed class PullRequest
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("headRefName")]
    public string HeadRefName { get; init; } = string.Empty;

    [JsonPropertyName("labels")]
    public List<GithubLabel> Labels { get; init; } = new();

    [JsonPropertyName("comments")]
    public List<GithubComment> Comments { get; init; } = new();

    [JsonPropertyName("closingIssuesReferences")]
    public List<ClosingIssueReference> ClosingIssuesReferences { get; init; } = new();

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;
}

