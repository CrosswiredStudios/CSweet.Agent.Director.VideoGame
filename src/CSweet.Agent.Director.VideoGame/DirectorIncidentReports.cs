using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    public override Task<IncidentOwnerReport?> GenerateIncidentOwnerReportAsync(ManagerIncidentContext review,
        AgentRuntimeContext context, CancellationToken token)
    {
        if (review.Incident.Status is not ("Resolved" or "Superseded") && review.Diagnostics.CurrentState is { } state &&
            state.Facts.Any(x => x.Kind == "WorkStageExecution" &&
                (x.Detail.Contains("Failed", StringComparison.Ordinal) || x.Detail.Contains("Blocked", StringComparison.Ordinal)) &&
                x.Detail.Contains("The QA compute operation is unavailable or its generation changed.", StringComparison.Ordinal)))
            return Task.FromResult<IncidentOwnerReport?>(ManagerIncidentCoordinator.FactualFallback(review)! with
            {
                Title = "QA stopped on an operation tracking mismatch",
                Summary = "QA could not continue because its saved compute operation was unavailable or belonged to a different environment generation. This error alone does not establish a broken browser environment or a product defect. The independent QA verdict remains incomplete.",
                Recovery = "Preserve the integrated source and completed technical review. Verify the current QA package and operation before retrying the stopped quality stage.",
                NextStep = "Producer: verify the QA update is active, then resume the existing quality stage. Recheck outstanding assertions against the exact integrated build.",
                RequiresOwnerInput = false, OwnerInputReason = null, Options = [], RecommendedOptionId = ""
            });
        return base.GenerateIncidentOwnerReportAsync(review, context, token);
    }
}
