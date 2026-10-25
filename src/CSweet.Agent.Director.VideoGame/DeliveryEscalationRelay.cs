using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame;

/// <summary>
/// A report raises a delivery blocker it can't clear (environment, tooling, scope, a stopped ticket) to me as its
/// manager. Those decisions are outside creative direction, so I don't answer or reject them: I pass them up my
/// reporting chain once, attributed, so the person who can decide hears about it. The decider replies to the
/// reporter directly with the reporter's own commands, which reporters accept from their reporting chain.
/// Passing a message on takes no action and grants nothing.
/// </summary>
internal static class DeliveryEscalationRelay
{
    private static readonly string[] Commands = ["`Retry ticket ", "`Amend ticket ", "`Replan ticket ", "`Retry staffing:"];
    private static readonly string[] Asks =
    [
        "needs your decision", "can't start", "has been blocked since", "has been marked failed since",
        "can't move it forward without a decision"
    ];
    internal const int MaximumForwardedLength = 3500;

    internal static bool IsDeliveryEscalation(CommunicationMessageReceivedEvent incoming, string message, bool fromManager) =>
        !fromManager &&
        string.Equals(incoming.Context?.GetValueOrDefault(CommunicationMessageContextKeys.SenderEmployeeType), "Agent", StringComparison.OrdinalIgnoreCase) &&
        Commands.Any(x => message.Contains(x, StringComparison.Ordinal)) &&
        Asks.Any(x => message.Contains(x, StringComparison.OrdinalIgnoreCase));

    internal static string ForwardMessage(string? senderName, string? senderRole, string message)
    {
        var who = string.IsNullOrWhiteSpace(senderName) ? "A team member" : senderName.Trim();
        if (!string.IsNullOrWhiteSpace(senderRole)) who += $" ({senderRole.Trim()})";
        var body = message.Trim();
        if (body.Length > MaximumForwardedLength) body = body[..(MaximumForwardedLength - 3)] + "...";
        return $"{who} raised a delivery blocker that's outside creative direction, so I'm passing it to you. " +
            $"Reply to {(string.IsNullOrWhiteSpace(senderName) ? "them" : senderName.Trim())} directly with one of the commands at the end; " +
            "they act on direction from their reporting chain.\n\n---\n\n" + body;
    }

    internal static string Acknowledgement(string? managerName) =>
        $"Received. This is a delivery decision outside creative direction, so I've passed it to " +
        $"{(string.IsNullOrWhiteSpace(managerName) ? "my manager" : managerName.Trim())}, who can reply to you directly. " +
        "Nothing else is needed from you until then.";

    internal static string IdempotencyKey(Guid messageId, string message) =>
        $"creative-delivery-escalation:{(messageId == Guid.Empty ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(message)))[..32] : messageId.ToString("N"))}";
}
