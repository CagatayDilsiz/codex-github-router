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

            var workflowTasks = await WorkflowService.CheckCompletedIssuesAsync(configuration, payload.Cwd);

            if (!workflowTasks.IsSuccessful)
            {
                await WriteBlockAsync(workflowTasks.Message);
                return 0;
            }

            if (workflowTasks.Tasks.Count > 0)
            {
                var tasks = workflowTasks.Tasks.Where(y => y.Type != TaskType.NewIssue).OrderBy(t => t.Type); // in hook service we do not handle new issue task type here but in else.

                var firstTask = tasks.FirstOrDefault(y => y.PullRequestNumber.HasValue);
                
                if (firstTask != null && firstTask.PullRequestNumber.HasValue && firstTask.Type == TaskType.ChangeRequest)
                {
                    await WriteAdditionalContextAsync(ContextPromptService.GetChangeRequestPrompt(firstTask.IssueNumber, firstTask.PullRequestNumber.Value));
                }
                else if (firstTask != null && firstTask.Type == TaskType.ReviewPRForOpenIssues)
                {
                    await WriteAdditionalContextAsync(ContextPromptService.GetReviewPRForOpenIssuesPrompt(tasks.Select(t => t.IssueNumber).ToArray()));
                }
                else
                {
                    await WriteBlockAsync("No actionable tasks found in the workflow.");                   
                }
                
                return 0;
            }
            else
            {
                var workflowResponse = await WorkflowService.CheckNewIssuesAsync(configuration, payload.Cwd);

                if (!workflowResponse.IsSuccessful || workflowResponse.Tasks.Count == 0)
                {
                    await WriteBlockAsync(workflowResponse.Message);
                    return 0;
                }           
            
                var nextIssue = workflowResponse.Tasks.First().IssueNumber;

                var context = ContextPromptService.GetNewIssuePrompt(nextIssue);

                await WriteAdditionalContextAsync(context);
            }

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