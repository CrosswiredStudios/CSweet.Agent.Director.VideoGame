using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PlanningDecisionNotificationTests
{
    [Fact]
    public void CompleteAuthoredPlanningRationaleHasNoArbitraryTextLimit()
    {
        var rationale = string.Concat(Enumerable.Repeat("Preserve exact approved scope and use current staffing facts. ", 200));
        var review = VideoGameDirectorAgent.ParsePlanningDirection(JsonSerializer.Serialize(new {
            optionId = "continue-current-plan", rationale, requiresOwner = false }), [new("continue-current-plan", "Continue", null)]);
        Assert.Equal(rationale, review.Rationale);
    }
    [Theory]
    [InlineData("Pending", "video-game.management-direction.v1", true)]
    [InlineData("Decided", "video-game.management-direction.v1", false)]
    [InlineData("Superseded", "video-game.management-direction.v1", false)]
    [InlineData("Pending", "video-game.asset-production.v1", false)]
    public void OnlyPendingManagementDirectionIsRelayed(string status, string type, bool expected)
    {
        var decision = JsonSerializer.Deserialize<DecisionRecord>("{}")! with {
            Id = Guid.NewGuid(), WorkstreamId = Guid.NewGuid(), Summary = "Set browser targets",
            Status = status, TypeKey = type
        };
        var notice = VideoGameDirectorAgent.PlanningDecisionNotice(decision, "business");
        Assert.Equal(expected, notice is not null);
        if (notice is not null)
        {
            Assert.Contains(decision.Id.ToString("D"), notice);
            Assert.Contains($"/organizations/business/projects/{decision.WorkstreamId:D}", notice);
            Assert.Contains("Set browser targets", notice);
            Assert.Contains("uncommitted", notice);
        }
    }

    [Fact]
    public void ExactDecisionNotificationIsRecognizedWithoutTreatingOrdinaryVisionTextAsApproval()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, VideoGameDirectorAgent.PlanningDecisionNotificationId($"Planning decision {id:D} requires direction"));
        Assert.Null(VideoGameDirectorAgent.PlanningDecisionNotificationId($"Accept the game vision. Planning decision {id:D}"));
        Assert.Null(VideoGameDirectorAgent.PlanningDecisionNotificationId("Planning decision invalid"));
        Assert.Null(VideoGameDirectorAgent.PlanningDecisionNotificationId(""));
    }
}
