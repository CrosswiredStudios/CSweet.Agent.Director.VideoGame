using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class ExactStaffingApprovalTests
{
    [Fact]
    public void LiveJustifiedNeedCannotApproveAMisclassifiedPackage()
    {
        var judgment = JsonSerializer.Deserialize<VideoGameDirectorAgent.StaffingJudgment>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "qfn-conditional-staffing-approval.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Throws<AgentModelJsonException>(() => VideoGameDirectorAgent.ValidateStaffingJudgment(judgment));
        var revision = judgment with { ApproveAsSubmitted = false, RequiredChanges = ["Replace the security role with Technical Director capacity."] };
        Assert.Equal(revision, VideoGameDirectorAgent.ValidateStaffingJudgment(revision));
        Assert.Contains(revision.RequiredChanges[0], VideoGameDirectorAgent.DescribeStaffingRevision(revision));
        Assert.Throws<AgentModelJsonException>(() => VideoGameDirectorAgent.ValidateStaffingJudgment(revision with { ApproveAsSubmitted = true }));
        Assert.True(VideoGameDirectorAgent.ValidateStaffingJudgment(revision with { ApproveAsSubmitted = true, RequiredChanges = [] }).ApproveAsSubmitted);
    }
}
