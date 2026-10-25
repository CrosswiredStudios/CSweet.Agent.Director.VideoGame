using System.Text.RegularExpressions;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Director.VideoGame;

internal enum GameDirectorInboundDisposition
{
    WorkflowInput,
    Acknowledge,
    StatusRequest,
    InformationQuestion,
    DurableAction
}

internal static partial class GameDirectorInteractionPolicy
{
    internal static GameDirectorInboundDisposition Classify(string message)
    {
        var value = message.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return GameDirectorInboundDisposition.Acknowledge;

        if (value.StartsWith("Decision:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("selected option:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("\nAnswer:", StringComparison.OrdinalIgnoreCase))
            return GameDirectorInboundDisposition.WorkflowInput;

        if (Acknowledgement().IsMatch(value))
            return GameDirectorInboundDisposition.Acknowledge;

        if (StatusRequest().IsMatch(value))
            return GameDirectorInboundDisposition.StatusRequest;

        if (DurableAction().IsMatch(value))
            return GameDirectorInboundDisposition.DurableAction;

        if (value.Contains('?') || InformationQuestion().IsMatch(value))
            return GameDirectorInboundDisposition.InformationQuestion;

        return GameDirectorInboundDisposition.WorkflowInput;
    }

    [GeneratedRegex(@"(?ix)^\s*(?:ok(?:ay)?|thanks?(?:\s+you)?|got\s+it|understood|sounds\s+good|great|perfect|acknowledged|confirmed)[.!\s]*$")]
    private static partial Regex Acknowledgement();

    [GeneratedRegex(@"(?ix)\b(?:status|progress\s+update|where\s+are\s+we|what\s+(?:is|'s)\s+blocking|what\s+are\s+you\s+working\s+on|current\s+phase|project\s+health)\b")]
    private static partial Regex StatusRequest();

    [GeneratedRegex(@"(?ix)\b(?:explore|investigate|research|review|evaluate|assess|prepare|draft|document|brainstorm|analy[sz]e|compare|produce|write|create|revise|update|plan)\b")]
    private static partial Regex DurableAction();

    [GeneratedRegex(@"(?ix)^\s*(?:what|how|why|when|where|who|which|explain|summari[sz]e|tell\s+me)\b")]
    private static partial Regex InformationQuestion();
}

internal static class GameDirectorAgenda
{
    internal const string VisionKind = "creative-director.vision.v1";
    internal const string StaffingKind = "creative-director.staffing-plan.v1";
    internal const string ChatActionKind = "creative-director.chat-action.v1";
    internal const string ProjectReviewKind = "creative-director.project-review.v1";
    internal const string CreativeRequestArtifactType = "creative-direction.request-response.v1";

    internal static string VisionCorrelation(Guid conversationId) =>
        $"{VisionKind}:{conversationId:N}";

    internal static string StaffingCorrelation(Guid conversationId) =>
        $"{StaffingKind}:{conversationId:N}";

    internal static string ChatActionCorrelation(Guid messageId) =>
        $"{ChatActionKind}:{messageId:N}";

    internal static string ProjectReviewCorrelation(Guid conversationId) =>
        $"{ProjectReviewKind}:{conversationId:N}";

    internal static bool IsVision(PersonalTodoItem item) =>
        item.CorrelationId?.StartsWith($"{VisionKind}:", StringComparison.Ordinal) == true ||
        item.Title.Equals("Build the high-level game design document", StringComparison.OrdinalIgnoreCase);

    internal static bool IsStaffing(PersonalTodoItem item) =>
        item.CorrelationId?.StartsWith($"{StaffingKind}:", StringComparison.Ordinal) == true ||
        item.Title.Equals("Create and submit the game-studio staffing plan", StringComparison.OrdinalIgnoreCase);

    internal static bool IsChatAction(PersonalTodoItem item) =>
        item.CorrelationId?.StartsWith($"{ChatActionKind}:", StringComparison.Ordinal) == true;

    internal static bool IsProjectReview(PersonalTodoItem item) =>
        item.CorrelationId?.StartsWith($"{ProjectReviewKind}:", StringComparison.Ordinal) == true;

    internal static Guid? ProjectReviewConversationId(PersonalTodoItem item)
    {
        if (!IsProjectReview(item)) return null;
        var suffix = item.CorrelationId![(ProjectReviewKind.Length + 1)..];
        if (!Guid.TryParseExact(suffix, "N", out var conversationId) ||
            item.SourceConversationId.HasValue && item.SourceConversationId != conversationId)
            return null;
        return conversationId;
    }

    internal static TimeSpan ProjectReviewCadence(GameDirectorPhase phase) =>
        phase == GameDirectorPhase.Oversight ? TimeSpan.FromDays(1) : TimeSpan.FromHours(4);

    internal static string RequestText(PersonalTodoItem item)
    {
        const string marker = "Requested action:\n";
        var description = item.Description ?? string.Empty;
        var markerAt = description.IndexOf(marker, StringComparison.Ordinal);
        return markerAt < 0 ? description.Trim() : description[(markerAt + marker.Length)..].Trim();
    }

    internal static string TaskTitle(string message)
    {
        var singleLine = Regex.Replace(message.Trim(), @"\s+", " ");
        if (singleLine.Length > 96) singleLine = $"{singleLine[..93]}...";
        return $"Creative request: {singleLine}";
    }
}
