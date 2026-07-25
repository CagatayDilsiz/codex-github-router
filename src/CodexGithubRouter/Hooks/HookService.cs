using System.Text.Json;
using CodexGithubRouter.Autonomous;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Prompts;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Hooks;


public static class HookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync()
    {
        try
        {
            var json = await Console.In.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(json))
            {
                await WriteBlockAsync("Could not read hook payload from stdin.");
                return 0;
            }

            var payload = JsonSerializer.Deserialize<HookPayload>(
                json,
                JsonOptions);

            if (payload is null)
            {
                await WriteBlockAsync("Could not deserialize hook payload.");
                return 0;
            }

            // If this executable is accidentally bound to another hook event
            // continue without any intervention.
            if (!string.Equals(payload.HookEventName,"UserPromptSubmit",StringComparison.Ordinal))
            {
                return 0;
            }

            if (!await AutonomousService.IsAutonomousAsync(payload.Cwd))
            {
                // If autonomous mode is disabled, do not intervene in the manual prompt.
                return 0;
            }

            var configuration = await WorkflowConfigurationService.LoadOrCreateAsync();

            var completedIssueFilters = await IssueFilterResolver.ByState(configuration,WorkflowState.Completed);

            if (completedIssueFilters is null)
            {
                await WriteBlockAsync("Could not resolve issue filters from workflow configuration.");
                return 0;
            }

            var completedIssues = await GitHubCliService.GetIssuesAsync(payload.Cwd, completedIssueFilters);

            if (completedIssues.Count > 0)
            {
                // for now, we will block, later we will check pr status and decide to continue or block based on that.
                await WriteBlockAsync("There are completed issues that need to be closed before proceeding.");
                return 0;
            }
            
            var openIssueFilters = await IssueFilterResolver.ByState(configuration, WorkflowState.Ready);

            if (openIssueFilters is null)
            {
                await WriteBlockAsync("Could not resolve open issue filters from workflow configuration.");
                return 0;
            }

            var openIssues = await GitHubCliService.GetIssuesAsync(payload.Cwd, openIssueFilters);

            if (openIssues.Count == 0)
            {
                await WriteBlockAsync("No open issues found for the current repository.");
                return 0;
            }
            
            var nextIssue = openIssues.First();
            
            var context = NewIssuePrompt.GetPrompt(nextIssue.Number);          
           

            await WriteAdditionalContextAsync(context);

            return 0;
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            await WriteBlockAsync("Hook payload is not valid JSON.");

            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());

            await WriteBlockAsync(
                $"Codex Github Router could not be run: {exception.Message}");

            return 0;
        }
    }

    private static Task WriteBlockAsync(string reason)
    {
        return WriteJsonAsync(new
        {
            decision = "block",
            reason
        });
    }

    private static Task WriteAdditionalContextAsync(string context)
    {
        return WriteJsonAsync(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "UserPromptSubmit",
                additionalContext = context
            }
        });
    }

    private static async Task WriteJsonAsync(object response)
    {
        var json = JsonSerializer.Serialize(response);

        await Console.Out.WriteLineAsync(json);
        await Console.Out.FlushAsync();
    }
}