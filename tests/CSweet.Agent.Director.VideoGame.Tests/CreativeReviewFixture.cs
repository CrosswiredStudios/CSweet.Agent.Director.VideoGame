using System.Text.Json;
namespace CSweet.Agent.Director.VideoGame.Tests;
internal static class CreativeReviewFixture
{
    public static JsonElement Accepted(GameDirectorOperatingState state, Guid document, Guid revision, string hash, bool requiresDirector = false) =>
        JsonSerializer.SerializeToElement(new {
            Request = new VideoGameDirectorAgent.CreativeReviewRequest(state.WorkstreamId!.Value, state.TeamId!.Value,
                state.AcceptedVision!.ArtifactId, state.AcceptedVision.ArtifactRevisionId, state.AcceptedVision.ArtifactRevisionHash,
                document, revision, hash, "Creative consistency"),
            Review = requiresDirector ? new VideoGameDirectorAgent.CreativeReviewResult("escalate", "Director should resolve planning scope", ["A playable build was requested before staffing"], ["Define future acceptance evidence"], true) : new VideoGameDirectorAgent.CreativeReviewResult("accepted", "The creative pillars and player experience are preserved.", [], [], false)
        });
}
