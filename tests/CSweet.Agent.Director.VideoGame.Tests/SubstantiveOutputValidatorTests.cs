using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Director.VideoGame.Tests
;

public sealed class SubstantiveOutputValidatorTests
{
    private static readonly string Body = new string('x', 900) + "\n";

    [Theory]
    [InlineData("| PR-2 | No `TODO`, `FIXME`, `HACK`, `XXX` comments in diff | Grep in review |")]
    [InlineData("No TODO, FIXME or HACK comments are allowed.")]
    [InlineData("Use placeholder art until the final sprites land; mastodon and todos are fine.")]
    [InlineData("The final palette is to be decided by the art director.")]
    [InlineData("```js\n// TODO: wire input\n```")]
    public void LegitimateContentIsNotAPlaceholder(string text) =>
        Assert.Null(SubstantiveOutputValidator.FindIssue(Body + text));

    [Theory]
    [InlineData("## Risks\nTODO: fill in")]
    [InlineData("| Owner | TBD |")]
    [InlineData("- Audio budget: TBD")]
    [InlineData("Lorem ipsum dolor sit amet")]
    [InlineData("[Insert diagram here]")]
    [InlineData("Release date (TBD)")]
    public void UnresolvedMarkersAreRejected(string text) =>
        Assert.StartsWith("The deliverable contains unresolved placeholder text",
            SubstantiveOutputValidator.FindIssue(Body + text));
}
