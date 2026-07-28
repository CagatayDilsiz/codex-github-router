using Xunit;

namespace CodexGithubRouter.Tests;

public sealed class WorkflowLifecycleTests
{
    [Fact]
    public Task Pull_request_transition_lifecycle_is_safe() => LegacyScenarios.AssertPullRequestTransitionLifecycleAsync();

    [Fact]
    public Task Claimed_work_recovery_is_safe() => LegacyScenarios.AssertClaimedWorkRecoveryAsync();

    [Fact]
    public Task Active_claim_route_orchestration_is_safe() => LegacyScenarios.AssertActiveClaimRouteOrchestrationAsync();
}
