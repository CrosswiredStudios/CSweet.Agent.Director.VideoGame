using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.PitchCollaboration;
using CrosswiredStudios.VideoGame.ProductionReview;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private static async Task<(ArtifactDocument Document, ArtifactRevision Revision, ProductionSources Sources)?> VerifyProductionSourcesAsync(
        PitchReview review, GameDirectorOperatingState state, AgentRuntimeContext context, CancellationToken token)
    {
        if (review.SourceEvidence is null) return null; // Legacy briefs retain their original source appendices.
        var exact = await ProductionSources.ReadAsync(review.SourceEvidence, state.WorkstreamId!.Value, state.TeamId!.Value, context, token);
        var pitch = state.AcceptedVision!;
        if (exact.Sources.Pitch.DocumentId != pitch.ArtifactId || exact.Sources.Pitch.RevisionId != pitch.ArtifactRevisionId ||
            exact.Sources.Pitch.Sha256 != pitch.ArtifactRevisionHash ||
            exact.Sources.Gdd.DocumentId != state.CreativeDesignArtifactId || exact.Sources.Gdd.RevisionId != state.CreativeDesignRevisionId)
            throw new InvalidOperationException("Production sources do not match the project's accepted pitch and GDD.");
        foreach (var source in new[] { exact.Sources.Pitch, exact.Sources.Gdd })
        {
            var accepted = await context.Platform.Artifacts.ReadAcceptedAsync(new(source.DocumentId, source.RevisionId, source.Sha256), token);
            if (accepted.Document.DocumentType != source.DocumentType || accepted.Revision.Content != source.Content)
                throw new InvalidOperationException("Production source content differs from its exact accepted original.");
        }
        return exact;
    }
}
