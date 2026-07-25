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

            var completedIssueTasks = await WorkflowService.CheckCompletedIssuesAsync(configuration, payload.Cwd);

           
            if (!completedIssueTasks.IsSuccessful)
            {
                await WriteBlockAsync(completedIssueTasks.Message);
                return 0;
            }

            var newIssueTask = await WorkflowService.CheckNewIssuesAsync(configuration, payload.Cwd);

            if (!newIssueTask.IsSuccessful)
            {
                await WriteBlockAsync(newIssueTask.Message);
                return 0;
            }

            var combinedTasks = new WorkflowResponse
            {
                IsSuccessful = true,
                Message = "Combined workflow tasks.",
                Tasks = completedIssueTasks.Tasks.Concat(newIssueTask.Tasks).ToList()
            };

            var blockingTypes = new HashSet<WorkflowItemType>
            {
                WorkflowItemType.AwaitingReview,
                WorkflowItemType.AwaitingMerge,
                WorkflowItemType.ClosedWithoutMerge,
                WorkflowItemType.UnknownPullRequestState
            };

            if (combinedTasks.Tasks.Any(y => y.Type != WorkflowItemType.Deferred))
            {
                // close any issues that are marked for closure before hook blocker
                var closingIssueTasks = combinedTasks.Tasks.Where(y => y.Type == WorkflowItemType.CloseIssue).ToList();

                foreach (var closingIssueTask in closingIssueTasks)
                {
                    await GitHubCliService.CloseIssueAsync(payload.Cwd, closingIssueTask.IssueNumber, CancellationToken.None);
                }

                // find the first hookblocker true task and write the message to the block output
                var hookBlockerTask = combinedTasks.Tasks.FirstOrDefault(task => blockingTypes.Contains(task.Type));

                if (hookBlockerTask != null)
                {
                    await WriteBlockAsync(hookBlockerTask.Status.Message);
                    return 0;
                }
                
                var changeRequestTask = combinedTasks.Tasks.FirstOrDefault(y => y.Type == WorkflowItemType.ChangeRequest);

                if (changeRequestTask != null && changeRequestTask.PullRequestNumber.HasValue)
                {
                    await WriteAdditionalContextAsync(ContextPromptService.GetChangeRequestPrompt(changeRequestTask.IssueNumber, changeRequestTask.PullRequestNumber.Value));
                    return 0;
                }

                var issuesNeedingPRLink = combinedTasks.Tasks.Where(t => t.Type == WorkflowItemType.LinkPullRequestsToIssues).Select(t => t.IssueNumber).ToList();

                if (issuesNeedingPRLink.Count > 0)
                {
                    await WriteAdditionalContextAsync(ContextPromptService.GetIssuesNeedPRLinkPrompt(issuesNeedingPRLink.ToArray()));
                    return 0;
                }

                var newIssueTaskToPrompt = combinedTasks.Tasks.FirstOrDefault(t => t.Type == WorkflowItemType.NewIssue);

                if (newIssueTaskToPrompt != null)
                {
                    var context = ContextPromptService.GetNewIssuePrompt(newIssueTaskToPrompt.IssueNumber);

                    await WriteAdditionalContextAsync(context);
                    return 0;
                }


                await WriteBlockAsync("No actionable workflow tasks found.");
                return 0;
            }
            else
            {
                if (combinedTasks.Tasks.All(t => t.Type == WorkflowItemType.Deferred))
                {
                    await WriteBlockAsync("All workflow tasks are deferred. No action is required at this time.");            
                }
                else
                {
                    await WriteBlockAsync("No actionable workflow tasks found.");
                    
                }

               return 0;
            }          
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