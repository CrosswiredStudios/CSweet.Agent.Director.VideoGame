using CSweet.Agent.SDK;
namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class PortfolioReportingTests
{
    [Theory]
    [InlineData("Daily", "2026-10-23T16:00:00Z", "2026-10-24T16:00:00Z")]
    [InlineData("Weekdays", "2026-10-23T16:00:00Z", "2026-10-26T16:00:00Z")]
    [InlineData("Weekly", "2026-10-23T16:00:00Z", "2026-10-30T16:00:00Z")]
    [InlineData("Daily", "2026-10-31T16:00:00Z", "2026-11-01T17:00:00Z")]
    public void Scheduled_reports_preserve_local_time_and_skip_missed_occurrences(string cadence, string after, string expected)
    {
        var prefs = new VideoGameDirectorAgent.ReportingPreferences(Guid.NewGuid(), cadence, "Both", "America/Los_Angeles");
        Assert.Equal(DateTimeOffset.Parse(expected), VideoGameDirectorAgent.NextReportAt(prefs, DateTimeOffset.Parse(after)));
    }

    [Theory]
    [InlineData("Milestones")]
    [InlineData("OnRequest")]
    [InlineData(null)]
    public void Non_recurring_preferences_never_create_a_timed_occurrence(string? cadence)
    {
        var prefs = new VideoGameDirectorAgent.ReportingPreferences(Guid.NewGuid(), cadence, "Messages", "UTC");
        Assert.Null(VideoGameDirectorAgent.NextReportAt(prefs, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Multiple_games_in_one_conversation_keep_independent_state_and_promotions()
    {
        var chat = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var a = new GameDirectorPortfolioEntry(VideoGameDirectorAgent.ProjectStateKey(null, chat, first), null, chat,
            null, null, "First", GameDirectorPhase.Discovery, DateTimeOffset.UtcNow) { IntakeId = first };
        var b = a with { IntakeId = second, StateKey = VideoGameDirectorAgent.ProjectStateKey(null, chat, second), WorkingTitle = "Second" };
        var entries = VideoGameDirectorAgent.MergePortfolioEntry([a], b);
        Assert.Equal(2, entries.Count);
        Assert.NotEqual(a.StateKey, b.StateKey);
        var project = Guid.NewGuid();
        entries = VideoGameDirectorAgent.MergePortfolioEntry(entries, a with { WorkstreamId = project,
            StateKey = VideoGameDirectorAgent.ProjectStateKey(project, chat, first) });
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, x => x.IntakeId == second && x.WorkstreamId is null);
        Assert.Contains(entries, x => x.IntakeId == first && x.WorkstreamId == project);
    }
}
