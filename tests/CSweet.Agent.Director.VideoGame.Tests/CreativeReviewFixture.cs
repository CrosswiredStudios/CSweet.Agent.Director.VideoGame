using System.Text.Json;
namespace CSweet.Agent.Director.VideoGame.Tests;
internal static class CreativeReviewFixture
{
    public static JsonElement Accepted(GameDirectorOperatingState state, Guid document, Guid revision, string hash) =>
        JsonSerializer.SerializeToElement(new {
            Request = new VideoGameDirectorAgent.CreativeReviewRequest(state.WorkstreamId!.Value, state.TeamId!.Value,
                state.AcceptedVision!.ArtifactId, state.AcceptedVision.ArtifactRevisionId, state.AcceptedVision.ArtifactRevisionHash,
                document, revision, hash, "Creative consistency"),
            Review = new VideoGameDirectorAgent.CreativeReviewResult("accepted", "The creative pillars and player experience are preserved.", [], [], false)
        });
}
