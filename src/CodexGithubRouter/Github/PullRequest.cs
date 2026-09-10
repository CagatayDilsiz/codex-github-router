using System.Text.Json;
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

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("author")]
    [JsonConverter(typeof(PullRequestAuthorConverter))]
    public GithubUser Author { get; init; } = new GithubUser();

    [JsonPropertyName("reviewRequests")]
    [JsonConverter(typeof(ReviewRequestsConverter))]
    public List<PullRequestReviewRequest> ReviewRequests { get; init; } = new();

    /// <summary>
    /// Logins of directly requested user reviewers (teams are excluded).
    /// </summary>
    public IReadOnlyList<string> RequestedUserReviewerLogins =>
        ReviewRequests
            .Where(request => !request.IsTeam && !string.IsNullOrWhiteSpace(request.ReviewerLogin))
            .Select(request => request.ReviewerLogin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsDirectlyReviewerRequested(string reviewerLogin) =>
        RequestedUserReviewerLogins.Any(login => string.Equals(login, reviewerLogin, StringComparison.OrdinalIgnoreCase));

    public string? GetReviewRequestId(string reviewerLogin) =>
        ReviewRequests
            .FirstOrDefault(request => !request.IsTeam && string.Equals(request.ReviewerLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase))
            ?.Id;

    /// <summary>
    /// True when the pull request has at least one review request that is exclusively a team
    /// request (no matching direct user request exists).
    /// </summary>
    public IReadOnlyList<string> RequestedTeamReviewerSlugs =>
        ReviewRequests
            .Where(request => request.IsTeam && !string.IsNullOrWhiteSpace(request.ReviewerSlug))
            .Select(request => request.ReviewerSlug!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// A single GitHub review request on a pull request. Only direct user review requests are
/// claimable by CGR; team review requests are exposed for diagnostics but not claimable.
/// </summary>
public sealed class PullRequestReviewRequest
{
    public string Id { get; init; } = string.Empty;

    public string ReviewerLogin { get; init; } = string.Empty;

    public string ReviewerSlug { get; init; } = string.Empty;

    public bool IsTeam { get; init; }
}

public sealed class PullRequestAuthorConverter : JsonConverter<GithubUser>
{
    public override GithubUser Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        var author = new GithubUser();
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
        {
            author.Login = login.GetString() ?? string.Empty;
        }
        else if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
        {
            // Unhonorable case: pull requests authored by a deleted account or a bot with no login.
        }

        return author;
    }

    public override void Write(Utf8JsonWriter writer, GithubUser value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("login", value.Login);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Tolerant deserializer for <c>gh</c> <c>reviewRequests</c> output. Different gh versions wrap
/// the review requests in a <c>{ "nodes": [...] }</c> GraphQL connection or return a plain array,
/// so both shapes are accepted. Requested reviewers may be users or teams.
/// </summary>
public sealed class ReviewRequestsConverter : JsonConverter<List<PullRequestReviewRequest>>
{
    public override List<PullRequestReviewRequest> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        IEnumerable<JsonElement> items;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            items = nodes.EnumerateArray().ToList();
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            items = root.EnumerateArray().ToList();
        }
        else
        {
            return new List<PullRequestReviewRequest>();
        }

        var requests = new List<PullRequestReviewRequest>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = TryReadString(item, "id");
            if (!item.TryGetProperty("requestedReviewer", out var reviewer) || reviewer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var typename = TryReadString(reviewer, "__typename");
            var login = TryReadString(reviewer, "login");
            var slug = TryReadString(reviewer, "slug");
            var name = TryReadString(reviewer, "name");
            var isTeam = string.Equals(typename, "Team", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(login) && (!string.IsNullOrWhiteSpace(slug) || !string.IsNullOrWhiteSpace(name)));

            requests.Add(new PullRequestReviewRequest
            {
                Id = id,
                ReviewerLogin = login,
                ReviewerSlug = slug,
                IsTeam = isTeam
            });
        }

        return requests;
    }

    public override void Write(Utf8JsonWriter writer, List<PullRequestReviewRequest> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var request in value)
        {
            writer.WriteStartObject();
            writer.WriteString("id", request.Id);
            writer.WritePropertyName("requestedReviewer");
            writer.WriteStartObject();
            writer.WriteString("__typename", request.IsTeam ? "Team" : "User");
            if (request.IsTeam)
            {
                writer.WriteString("slug", request.ReviewerSlug);
            }
            else
            {
                writer.WriteString("login", request.ReviewerLogin);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string TryReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}