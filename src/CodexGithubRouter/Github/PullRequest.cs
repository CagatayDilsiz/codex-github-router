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

    [JsonPropertyName("reviews")]
    [JsonConverter(typeof(PullRequestReviewsConverter))]
    public List<PullRequestReview> Reviews { get; init; } = new();

    [JsonPropertyName("statusCheckRollup")]
    [JsonConverter(typeof(StatusCheckRollupConverter))]
    public List<CheckRun> StatusCheckRollup { get; init; } = new();

    /// <summary>
    /// Native GitHub mergeability signal: <c>MERGEABLE</c>, <c>UNMERGEABLE</c> or <c>UNKNOWN</c>.
    /// An absent/unknown value is intentionally conservative: the router never advances work on an
    /// unverifiable merge state.
    /// </summary>
    [JsonPropertyName("mergeable")]
    public string Mergeable { get; init; } = string.Empty;

    /// <summary>
    /// Native GitHub review decision for the pull request: <c>APPROVED</c>, <c>CHANGES_REQUESTED</c>
    /// or <c>REVIEW_REQUIRED</c>. An absent/unknown value is treated as no review signal.
    /// </summary>
    [JsonPropertyName("reviewDecision")]
    public string ReviewDecision { get; init; } = string.Empty;

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
    /// Node ID of the most recent review <em>submitted</em> by the given reviewer, if any. This is
    /// the production-available review-cycle marker: <c>gh pr view --json reviews</c> exports the
    /// submitted-review node ID but never the review-request node ID, so a submit followed by a
    /// fast re-request is recognized through the submitted-review identity instead. Only genuinely
    /// submitted reviews count — draft <c>PENDING</c> reviews carry no submitted timestamp and must
    /// never become a cycle marker.
    /// </summary>
    public string? GetLatestReviewId(string reviewerLogin) =>
        Reviews
            .Where(review => string.Equals(review.AuthorLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(review.Id) &&
                review.SubmittedAt != default &&
                !string.Equals(review.State, "PENDING", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(review => review.SubmittedAt)
            .Select(review => review.Id)
            .FirstOrDefault();

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

/// <summary>
/// A single check or status entry on a pull request, normalized from <c>gh pr view --json statusCheckRollup</c>.
/// Both GraphQL shapes are folded into one model: <c>__typename == "CheckRun"</c> entries carry
/// <c>name</c>/<c>status</c>/<c>conclusion</c>, while <c>__typename == "StatusContext"</c> entries
/// carry <c>context</c>/<c>state</c>. <see cref="StatusCheckRollupConverter"/> normalizes status
/// contexts into this same shape so the native-signal evaluator has a single, deterministic view.
/// </summary>
public sealed class CheckRun
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Normalized run status: <c>QUEUED</c>, <c>IN_PROGRESS</c> or <c>COMPLETED</c>. Status contexts
    /// map <c>PENDING</c>/<c>EXPECTED</c> to <c>QUEUED</c> and unknown states to <c>QUEUED</c> so the
    /// evaluator fails conservative on any non-completed run.
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Normalized conclusion for completed runs: <c>SUCCESS</c>, <c>FAILURE</c>, <c>NEUTRAL</c>,
    /// <c>CANCELLED</c>, <c>SKIPPED</c>, <c>TIMED_OUT</c>, <c>ACTION_REQUIRED</c> or <c>ERROR</c>.
    /// Empty when the run has not completed.
    /// </summary>
    public string Conclusion { get; init; } = string.Empty;

    public bool IsRequired { get; init; }
}

/// <summary>
/// A single submitted review on a pull request. Only the identity fields needed for review-cycle
/// detection are modeled: the review node ID and the submitting author. The submitted-review node
/// ID is the production-available review-cycle marker.
/// </summary>
public sealed class PullRequestReview
{
    public string Id { get; init; } = string.Empty;

    public string AuthorLogin { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public DateTimeOffset SubmittedAt { get; init; }
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
/// Tolerant deserializer for <c>gh</c> <c>reviewRequests</c> output. Production <c>gh</c> exports
/// a <em>flat</em> array of reviewer objects:
/// <c>[{ "__typename": "User", "login": "sergiou87" }]</c> (or <c>"Team"</c>/{ <c>slug</c>})
/// with no review-request node ID and no <c>requestedReviewer</c> wrapper. Some gh/GraphQL
/// versions wrap the requests in a <c>{ "nodes": [...] }</c> connection where each node is
/// <c>{ "id": "...", "requestedReviewer": { ... } }</c>. Both shapes are accepted so request
/// discovery never silently drops directly requested reviewers.
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

            // The GraphQL connection shape wraps the reviewer in a "requestedReviewer" object and
            // carries the review-request node id on the outer node. The production flat shape IS
            // the reviewer object itself and has no review-request id.
            JsonElement reviewer;
            string id;
            if (item.TryGetProperty("requestedReviewer", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            {
                reviewer = wrapped;
                id = TryReadString(item, "id");
            }
            else
            {
                reviewer = item;
                id = string.Empty;
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

/// <summary>
/// Tolerant deserializer for <c>gh</c> <c>reviews</c> output. Production <c>gh pr view --json reviews</c>
/// exports a flat array of submitted reviews with the review node ID, the submitting author, state and
/// <c>submittedAt</c>; a GraphQL connection shape (<c>{ "nodes": [...] }</c>) is also accepted.
/// </summary>
public sealed class PullRequestReviewsConverter : JsonConverter<List<PullRequestReview>>
{
    public override List<PullRequestReview> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
            return new List<PullRequestReview>();
        }

        var reviews = new List<PullRequestReview>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string authorLogin = string.Empty;
            if (item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            {
                authorLogin = TryReadString(author, "login");
            }

            DateTimeOffset submittedAt = default;
            if (item.TryGetProperty("submittedAt", out var submitted) && submitted.ValueKind == JsonValueKind.String)
            {
                DateTimeOffset.TryParse(submitted.GetString(), out submittedAt);
            }

            reviews.Add(new PullRequestReview
            {
                Id = TryReadString(item, "id"),
                AuthorLogin = authorLogin,
                State = TryReadString(item, "state"),
                SubmittedAt = submittedAt
            });
        }

        return reviews;
    }

    public override void Write(Utf8JsonWriter writer, List<PullRequestReview> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var review in value)
        {
            writer.WriteStartObject();
            writer.WriteString("id", review.Id);
            writer.WritePropertyName("author");
            writer.WriteStartObject();
            writer.WriteString("login", review.AuthorLogin);
            writer.WriteEndObject();
            writer.WriteString("state", review.State);
            writer.WriteString("submittedAt", review.SubmittedAt.ToString("O"));
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

/// <summary>
/// Tolerant deserializer for <c>gh</c> <c>statusCheckRollup</c> output. Production <c>gh pr view --json statusCheckRollup</c>
/// exports a flat array of <c>{ "__typename": "CheckRun", ... }</c> and <c>{ "__typename": "StatusContext", ... }</c>
/// entries; a GraphQL connection shape (<c>{ "nodes": [...] }</c>) is also accepted. Status contexts are normalized
/// into <see cref="CheckRun"/> using <see cref="NormalizeStatusContextState"/> so a single conservative evaluator
/// consumes both.
/// </summary>
public sealed class StatusCheckRollupConverter : JsonConverter<List<CheckRun>>
{
    public override List<CheckRun> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
            return new List<CheckRun>();
        }

        var runs = new List<CheckRun>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var typename = TryReadString(item, "__typename");
            if (string.Equals(typename, "StatusContext", StringComparison.OrdinalIgnoreCase))
            {
                var (status, conclusion) = NormalizeStatusContextState(TryReadString(item, "state"));
                runs.Add(new CheckRun
                {
                    Name = TryReadString(item, "context"),
                    Status = status,
                    Conclusion = conclusion,
                    IsRequired = TryReadBool(item, "isRequired")
                });
                continue;
            }

            runs.Add(new CheckRun
            {
                Name = TryReadString(item, "name"),
                Status = TryReadString(item, "status"),
                Conclusion = TryReadString(item, "conclusion"),
                IsRequired = TryReadBool(item, "isRequired")
            });
        }

        return runs;
    }

    public override void Write(Utf8JsonWriter writer, List<CheckRun> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var run in value)
        {
            writer.WriteStartObject();
            writer.WriteString("__typename", "CheckRun");
            writer.WriteString("name", run.Name);
            writer.WriteString("status", run.Status);
            writer.WriteString("conclusion", run.Conclusion);
            writer.WriteBoolean("isRequired", run.IsRequired);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Normalizes a StatusContext <c>state</c> into (status, conclusion). <c>SUCCESS</c> becomes a
    /// completed passing run, <c>FAILURE</c>/<c>ERROR</c> become completed failures, and every
    /// non-consumed state (including <c>PENDING</c>/<c>EXPECTED</c> and unknown values) becomes
    /// queued so the evaluator fails conservative on anything that is not demonstrably complete.
    /// </summary>
    private static (string Status, string Conclusion) NormalizeStatusContextState(string state)
    {
        if (string.Equals(state, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return ("COMPLETED", "SUCCESS");
        }

        if (string.Equals(state, "FAILURE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return ("COMPLETED", "FAILURE");
        }

        return ("QUEUED", string.Empty);
    }

    private static string TryReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool TryReadBool(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        return false;
    }
}