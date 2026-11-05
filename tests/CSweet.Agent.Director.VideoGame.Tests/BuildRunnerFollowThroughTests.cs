namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class BuildRunnerFollowThroughTests
{
    [Fact]
    public void Setup_authorization_names_the_remaining_action_and_binds_business_selection()
    {
        var business = Guid.NewGuid();
        var message = VideoGameDirectorAgent.BuildRunnerFollowThrough("restore-capacity", business);
        Assert.Contains($"/settings/toolchains?businessId={business:D}", message);
        Assert.Contains("does not install or certify", message);
        Assert.Contains("review the provider, environment and Office image", message);
        Assert.Contains("recheck current eligibility", message);
    }

    [Fact]
    public void Pause_and_custom_direction_do_not_claim_or_request_setup()
    {
        var paused = VideoGameDirectorAgent.BuildRunnerFollowThrough("pause", Guid.NewGuid());
        Assert.Contains("remains paused", paused); Assert.DoesNotContain("/settings/toolchains", paused);
        var custom = VideoGameDirectorAgent.BuildRunnerFollowThrough("other", Guid.NewGuid());
        Assert.DoesNotContain("authorized", custom); Assert.Contains("does not install or certify", custom);
    }
}
