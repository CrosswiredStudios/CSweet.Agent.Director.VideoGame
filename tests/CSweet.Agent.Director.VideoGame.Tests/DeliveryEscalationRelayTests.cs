using CSweet.Agent.SDK;

namespace CSweet.Agent.Director.VideoGame.Tests;

/// <summary>
/// The Producer raised VGF943299B17-6 to Naomi; it is not a creative question, and previously she answered that it
/// was outside creative direction, so the blocker went no further. A report's delivery blocker must reach the CEO.
/// </summary>
public sealed class DeliveryEscalationRelayTests
{
    private const string Escalation = "VGF943299B17-6 has been blocked since 2026-10-02 22:50 UTC, and none of my recovery steps apply, " +
        "so the team can't move it forward without a decision.\n\nnode: command not found\n\n" +
        "Once the cause is addressed, reply `Retry ticket VGF943299B17-6: <what changed>`, or `Amend ticket VGF943299B17-6: <your decision>`.";

    private static CommunicationMessageReceivedEvent Incoming(string type = "Agent") =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString(), "user", Escalation, new Dictionary<string, string>
        {
            [CommunicationMessageContextKeys.SenderEmployeeType] = type,
            [CommunicationMessageContextKeys.SenderDisplayName] = "Gabriel Reyes",
            [CommunicationMessageContextKeys.SenderRole] = "Video Game Producer"
        }, Guid.NewGuid(), 0, Guid.NewGuid());

    [Fact]
    public void A_reports_delivery_blocker_is_forwarded_up_with_attribution_and_the_commands()
    {
        Assert.True(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), Escalation, fromManager: false));
        var forwarded = DeliveryEscalationRelay.ForwardMessage("Gabriel Reyes", "Video Game Producer", Escalation);
        Assert.StartsWith("Gabriel Reyes (Video Game Producer) raised a delivery blocker", forwarded);
        Assert.Contains("Reply to Gabriel Reyes directly", forwarded);
        Assert.EndsWith(Escalation, forwarded);
        Assert.Contains("Matt", DeliveryEscalationRelay.Acknowledgement("Matt"));
    }

    [Fact]
    public void A_producer_access_refusal_is_relayed_too()
    {
        // Producer 2.18.2 wording when the platform denies it project delivery access.
        const string access = "Prism Break delivery can't start: the platform denied me `work.delivery.read.v1`, so I can't staff, dispatch or " +
            "recover its tickets. Nothing I retry can fix this, so it needs your decision.\n\nAdd me under Projects → Prism Break → Manage members. " +
            "Once it's fixed you can reply `Retry staffing: <what changed>` to have me recheck now.";
        Assert.True(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), access, fromManager: false));
        Assert.False(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), "Reply `Retry staffing: done` when ready.", fromManager: false));
    }

    [Fact]
    public void Ordinary_messages_are_not_relayed()
    {
        Assert.False(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), Escalation, fromManager: true));
        Assert.False(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming("Human"), Escalation, fromManager: false));
        Assert.False(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), "Can you review the art direction for level 2?", fromManager: false));
        Assert.False(DeliveryEscalationRelay.IsDeliveryEscalation(Incoming(), "Reply `Retry ticket X-1: done` when ready.", fromManager: false));
    }

    [Fact]
    public void Forwarding_is_bounded_and_idempotent_per_message()
    {
        var forwarded = DeliveryEscalationRelay.ForwardMessage(null, null, new string('x', 10000));
        Assert.True(forwarded.Length < DeliveryEscalationRelay.MaximumForwardedLength + 400);
        var id = Guid.NewGuid();
        Assert.Equal(DeliveryEscalationRelay.IdempotencyKey(id, "a"), DeliveryEscalationRelay.IdempotencyKey(id, "b"));
        Assert.Equal(DeliveryEscalationRelay.IdempotencyKey(Guid.Empty, "a"), DeliveryEscalationRelay.IdempotencyKey(Guid.Empty, "a"));
        Assert.True(DeliveryEscalationRelay.IdempotencyKey(Guid.Empty, "a").Length <= 128);
    }
}
