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
}