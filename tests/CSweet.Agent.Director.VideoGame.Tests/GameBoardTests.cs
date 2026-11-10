namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class GameBoardTests
{
    [Theory]
    [InlineData(null, "Game Pitch \u2014 \"Prism Break\"", "Prism Break")]
    [InlineData(null, "Prism Break \u2014 Official Game Pitch", "Prism Break")]
    [InlineData(null, "Official Game Pitch: Shatterwave", "Shatterwave")]
    [InlineData(null, "Shatterwave", "Shatterwave")]
    [InlineData(null, "Spider-Bot Rally", "Spider-Bot Rally")]
    [InlineData("Neon Drift", "Game Pitch \u2014 \"Prism Break\"", "Neon Drift")]
    [InlineData(null, "Game Pitch", "Video game")]
    [InlineData("  ", "", "Video game")]
    public void BoardIsNamedAfterTheGameNotThePitchDocument(string? workingTitle, string pitchHeading, string expected) =>
        Assert.Equal(expected, VideoGameDirectorAgent.GameBoardName(workingTitle, pitchHeading));

    [Fact]
    public void BoardNameIsBoundedToThePlatformLimit() =>
        Assert.Equal(160, VideoGameDirectorAgent.GameBoardName(new string('a', 400), "").Length);

    [Fact]
    public void OnlyThisGamesOwnUnboundCardsAreBoundToTheNewBoard()
    {
        var self = Guid.NewGuid();
        var report = Guid.NewGuid();
        var intake = Guid.NewGuid();
        var pitch = Card(self, "Create the official game pitch", new());
        var staffing = Card(self, "Create and submit the game-studio staffing plan", new());
        var fingerprinted = Card(self, "Intake card", new(SourceFingerprint: intake.ToString("D")));
        var otherGame = Card(self, "Another game's pitch", new());
        var alreadyBound = Card(self, "Already on a board", new(BoardId: Guid.NewGuid(), SourceFingerprint: intake.ToString("D")));
        var inProject = Card(self, "Project work", new(WorkstreamId: Guid.NewGuid(), SourceFingerprint: intake.ToString("D")));
        var reportsCard = Card(report, "A report's card", new(SourceFingerprint: intake.ToString("D")));
        var directory = new CSweet.WorkManagement.Contracts.PersonalTodoDirectory(
        [
            new(Guid.NewGuid(), self, "Marcus Vale", null, null, 1, [pitch, staffing, fingerprinted, otherGame, alreadyBound, inProject]),
            new(Guid.NewGuid(), report, "Gabriel Reyes", self, "Marcus Vale", 1, [reportsCard])
        ], self);

        // A game started from chat has no intake: the cards are found by the ids the state records.
        var fromChat = new GameDirectorOperatingState { VisionTodoId = pitch.Id, StaffingTodoId = staffing.Id };
        var fromIntake = fromChat with { IntakeId = intake };

        Assert.Equal(new[] { pitch.Id, staffing.Id },
            VideoGameDirectorAgent.CardsCreatedBeforeTheBoard(directory, fromChat).Select(x => x.Id).ToArray());
        Assert.Equal(new[] { pitch.Id, staffing.Id, fingerprinted.Id },
            VideoGameDirectorAgent.CardsCreatedBeforeTheBoard(directory, fromIntake).Select(x => x.Id).ToArray());
        Assert.Empty(VideoGameDirectorAgent.CardsCreatedBeforeTheBoard(directory, new GameDirectorOperatingState()));
    }

    private static CSweet.WorkManagement.Contracts.PersonalTodoItem Card(
        Guid owner, string title, CSweet.WorkManagement.Contracts.PersonalTodoWorkContext? context) =>
        new(Guid.NewGuid(), Guid.NewGuid(), owner, owner, "Owner", title, "", "Completed", "High", 1024, 1, null, null, null,
            [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { WorkContext = context };

    [Fact]
    public void StatusNamesBlockedLeadershipWorkWithItsReason()
    {
        var self = Guid.NewGuid();
        var board = Guid.NewGuid();
        var blocked = Card(self, "Get the Producer's project proposal approved", new(BoardId: board)) with
            { Status = "Blocked", BlockReason = "A budget figure is missing." };
        var waiting = Card(self, "Create the official game pitch", new(BoardId: board)) with { Status = "Running" };
        var otherGame = Card(self, "Another game", new(BoardId: Guid.NewGuid())) with { Status = "Blocked" };
        var directory = new CSweet.WorkManagement.Contracts.PersonalTodoDirectory(
            [new(Guid.NewGuid(), self, "Marcus Vale", null, null, 1, [blocked, waiting, otherGame])], self);

        var cards = VideoGameDirectorAgent.BlockedLeadershipCards(directory, new GameDirectorOperatingState { BoardId = board });
        var status = VideoGameDirectorAgent.BlockedLeadershipStatus(cards);

        Assert.Equal(new[] { blocked.Id }, cards.Select(x => x.Id).ToArray());
        Assert.Contains("Blocked leadership work", status, StringComparison.Ordinal);
        Assert.Contains("Get the Producer's project proposal approved", status, StringComparison.Ordinal);
        Assert.Contains("budget figure", status, StringComparison.Ordinal);
        Assert.Equal(string.Empty, VideoGameDirectorAgent.BlockedLeadershipStatus([]));
    }
}
