using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class DirectorIncidentReportTests
{
    [Fact]
    public async Task Current_operation_mismatch_does_not_request_owner_capacity_repair_or_another_model_call()
    {
        var now = DateTimeOffset.UtcNow; var self = Guid.NewGuid();
        var incident = new ManagementIncident(Guid.NewGuid(), Guid.NewGuid(), self, self, "Open", "QA stopped", null,
            now, now, now.AddMinutes(15), 1, "", "", "", "", []);
        var review = new ManagerIncidentContext(incident, new([], null, []) {
            CurrentState = new(new string('a', 64), "Prism Break", [new("WorkStageExecution", Guid.NewGuid(), now,
                "quality: Failed. The QA compute operation is unavailable or its generation changed.")], []) }, "test");
        var report = await new VideoGameDirectorAgent().GenerateIncidentOwnerReportAsync(review, new AgentTestRuntime().CreateContext(), default);
        ManagerIncidentCoordinator.ValidateReport(report!);
        Assert.False(report!.RequiresOwnerInput); Assert.Empty(report.Options);
        Assert.Contains("independent QA verdict remains incomplete", report.Summary);
        Assert.Contains("existing quality stage", report.NextStep);
    }
}
