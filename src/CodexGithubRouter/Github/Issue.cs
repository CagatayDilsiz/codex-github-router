using System.Text.Json.Serialization;

namespace CodexGithubRouter.GitHub;

public class Issue
{
    [JsonPropertyName("number")]
    public int Number { get; set; }
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("labels")]
    public List<GithubLabel> Labels { get; set; } = new List<GithubLabel>();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("closedByPullRequestsReferences")]
    public List<ClosingIssueReference> ClosingPullRequestsReferences { get; set; } = new List<ClosingIssueReference>();
}

public sealed class GithubLabel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;
}

public sealed class GithubComment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GithubUser Author { get; set; } = new GithubUser();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("authorAssociation")]
    public string AuthorAssociation { get; set; } = string.Empty;
}

public sealed class GithubUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
}

public sealed class ClosingIssueReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int Number { get; set; }    

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

