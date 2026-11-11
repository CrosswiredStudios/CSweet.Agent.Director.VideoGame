using CrosswiredStudios.VideoGame.PitchCollaboration;

namespace CSweet.Agent.Director.VideoGame.Tests;

public sealed class CreativeReviewReasonTests
{
    [Theory]
    [InlineData("Creative review did not return actionable, consistent findings.", true, null)]
    [InlineData("Creative review did not return actionable, consistent findings. Reason: checklist is missing scope", true, "checklist is missing scope")]
    [InlineData("Creative review did not return actionable, consistent findings.Reason: glued", false, null)]
    [InlineData("Approved scope is unavailable", false, null)]
    [InlineData(null, false, null)]
    public void Fixed_text_with_an_optional_reason_is_recognized(string? content, bool invalid, string? reason)
    {
        Assert.Equal(invalid, VideoGameDirectorAgent.IsInvalidCreativeReview(content));
        Assert.Equal(reason, VideoGameDirectorAgent.InvalidCreativeReviewReason(content));
    }
}

public sealed class DirectorReviewNormalizationTests
{
    [Fact]
    public void Missing_suggestion_is_derived_and_unbounded_answers_are_named()
    {
        var review = PitchProtocol.NormalizeDirectorReview(new(true, "Accept the brief as planned.", null!));
        Assert.Equal("", review.SuggestedMarkdown);
        Assert.Null(review.OwnerQuestion);
        var error = Assert.Throws<CSweet.Agent.SDK.AgentModelJsonException>(() =>
            PitchProtocol.NormalizeDirectorReview(new(false, "", "", RequiresOwner: true)));
        Assert.Contains("guidance is empty", error.Message);
        Assert.Contains("requiresOwner needs one focused ownerQuestion", error.Message);
    }
}
