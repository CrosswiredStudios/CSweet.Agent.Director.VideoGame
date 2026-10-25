using System.Text.RegularExpressions;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    internal static IReadOnlyList<PlanningConstraintEvidence> CapturePlanningConstraints(string message, Guid messageId)
    {
        if (messageId == Guid.Empty) return [];
        return Regex.Split(message, @"(?<=[.!?])\s+|[\r\n]+")
            .Select(x => x.Trim()).Where(x => x.Length > 0)
            .Where(x => Regex.IsMatch(x, @"\b(deadline|budget|forecast|quality.gated|external date|resource limit|reserved decision|spending|target date)\b", RegexOptions.IgnoreCase))
            .Take(8).Select(x => new PlanningConstraintEvidence(x[..Math.Min(x.Length, 2000)], messageId,
                Regex.IsMatch(x, @"\b(every project|all projects|future projects|standing preference|by default)\b", RegexOptions.IgnoreCase)))
            .ToList();
    }

    internal static string PlanningContext(ManagerPreferenceProfile preferences) =>
        "Delivery is forecast-driven. Missing deadlines never block planning or staffing proposals. " +
        "No external date constraint has been imposed unless explicitly recorded below. " +
        "Spending and release approvals remain separate. Feasibility and estimates refine completion forecasts.\n" +
        string.Join("\n", preferences.PlanningConstraints.Select(x =>
            $"Manager evidence {x.MessageId:D} ({(x.StandingPreference ? "explicit standing preference" : "this project only")}): {x.Text}"));
}
