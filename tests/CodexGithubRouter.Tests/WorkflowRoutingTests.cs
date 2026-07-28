using Xunit;

namespace CodexGithubRouter.Tests;

public sealed class WorkflowRoutingTests
{
    [Fact]
    public Task Single_working_issue_without_pull_request_resumes() => LegacyScenarios.AssertSingleWorkingIssueWithoutPullRequestAsync();

    [Fact]
    public Task Multiple_working_issues_block() => LegacyScenarios.AssertMultipleWorkingIssuesBlockAsync();

    [Fact]
    public Task Working_issue_with_open_pull_request_is_evaluated() => LegacyScenarios.AssertWorkingIssueWithOpenPullRequestAsync();

    [Fact]
    public void Resume_prompt_is_safe() => LegacyScenarios.AssertResumePromptIsSafe();

    [Fact]
    public void Hook_output_prioritizes_resume_work() => LegacyScenarios.AssertHookOutputResumesWorkingIssueBeforeReadyIssue();

    [Fact]
    public void Hook_route_precedence_is_preserved() => LegacyScenarios.AssertHookRoutePrecedence();

    [Fact]
    public void Workflow_label_conflicts_are_safe() => LegacyScenarios.AssertWorkflowLabelConflictResolution();

    [Fact]
    public Task Pull_request_label_conflicts_are_safe() => LegacyScenarios.AssertPullRequestLabelConflictHandlingAsync();

    [Fact]
    public void Issue_alias_search_uses_or_semantics() => LegacyScenarios.AssertIssueAliasSearchUsesOrSemantics();

    [Fact]
    public void Version_output_is_normalized() => LegacyScenarios.AssertVersionNormalization();

    [Fact]
    public void Prompt_contracts_are_language_independent() => LegacyScenarios.AssertClarificationPromptIsLanguageIndependent();

    [Fact]
    public void Repository_gate_configuration_is_preserved() => LegacyScenarios.AssertRepositoryGateConfiguration();

    [Fact]
    public Task Repository_gate_evaluation_covers_terminal_and_current_work() => LegacyScenarios.AssertRepositoryGateEvaluationAsync();
}
