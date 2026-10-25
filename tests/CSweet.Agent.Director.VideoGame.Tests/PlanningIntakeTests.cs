using CSweet.Agent.SDK;
using CSweet.Memory;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PlanningIntakeTests
{
    [Fact]
    public void MissingDeadlineUsesForecastsWithoutInventingSpendingAuthority()
    {
        var context = VideoGameDirectorAgent.PlanningContext(new());
        Assert.Contains("Missing deadlines never block", context);
        Assert.Contains("Spending and release approvals remain separate", context);
    }

    [Fact]
    public void ProjectConstraintsAndStandingPreferencesKeepExactEvidenceAndSeparateMemoryScope()
    {
        var id = Guid.NewGuid();
        var message = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString("D"), "owner",
            "Budget $500 for this project; external date constraint: conference demo November 1.\nFor future projects, use forecasts unless I supply a deadline.",
            new Dictionary<string, string>(), Guid.NewGuid(), 1, id);
        var preferences = VideoGameDirectorAgent.UpdateManagerPreferences(new(), message.Message, id, [], true);
        Assert.Equal(2, preferences.PlanningConstraints.Count);
        Assert.False(preferences.PlanningConstraints[0].StandingPreference);
        Assert.True(preferences.PlanningConstraints[1].StandingPreference);
        Assert.All(preferences.PlanningConstraints, x => Assert.Equal(id, x.MessageId));
        var memories = VideoGameDirectorAgent.BuildExplicitMemoryProposals(message, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var standing = Assert.Single(memories, x => x.Scope == MemoryScope.User);
        Assert.Contains("For future projects", standing.Content);
        Assert.DoesNotContain("$500", standing.Content);
        var project = Assert.Single(memories, x => x.Scope == MemoryScope.Tenant);
        Assert.Contains("$500", project.Content);
        Assert.Contains(message.ConversationId, project.Content);
    }
}
