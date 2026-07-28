using Xunit;

namespace CodexGithubRouter.Tests;

public sealed class WorkClaimTests
{
    [Fact]
    public Task Claims_are_acquired_concurrently_and_released_safely() => LegacyScenarios.AssertWorkClaimsAsync();

    [Fact]
    public void Passive_review_does_not_block_new_work() => LegacyScenarios.AssertPassiveReviewDoesNotBlockNewWork();

    [Fact]
    public void Claim_reconciliation_preserves_work_identity() => LegacyScenarios.AssertWorkClaimReconciliation();

    [Fact]
    public void Claim_owner_remains_routing_authority() => LegacyScenarios.AssertClaimRoutingAuthority();
}
