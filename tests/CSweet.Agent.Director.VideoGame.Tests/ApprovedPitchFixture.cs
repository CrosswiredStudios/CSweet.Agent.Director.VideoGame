using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame.Tests;

internal sealed class ApprovedPitchFixture(Guid producer)
{
    internal AcceptedGameVision Pitch { get; } = new(1, "pitch-digest", "Approved bounded game", Guid.NewGuid(),
        Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
    internal AgentOperatingStateResponse State => new(Guid.NewGuid(), VideoGameDirectorAgent.StateKey, "test", 1,
        "Active", new Dictionary<string,string>(), [], "pitch", [], Guid.NewGuid(),
        JsonSerializer.SerializeToElement(new GameDirectorOperatingState { AcceptedVision = Pitch, ProducerEmployeeId = producer }),
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    internal ArtifactDocument Document => new(Pitch.ArtifactId, "Official Game Pitch", "video-game.vision.v1",
        "Approved", Pitch.ArtifactRevisionId, null, Pitch.ArtifactRevisionId, null, null,
        [new(Pitch.ArtifactRevisionId, 1, null, Pitch.Markdown, Pitch.ArtifactRevisionHash, "Accepted", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
}
