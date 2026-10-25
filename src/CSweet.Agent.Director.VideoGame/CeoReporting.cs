using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.Director.VideoGame;

public sealed partial class VideoGameDirectorAgent
{
    private const string ReportingKey = "director-ceo-reporting";
    private const string ReportingTodo = "director-ceo-report";
    internal sealed record ReportingPreferences(Guid ConversationId, string? Cadence = null, string? Format = null,
        string? TimeZone = null, string LocalTime = "09:00", DateTimeOffset? NextDue = null, string? LastMilestoneDigest = null);

    private static async Task<UserQuestionResponse> AskReportingCadenceAsync(Guid chat, Guid source, AgentRuntimeContext context, CancellationToken token)
    {
        var anchor = await context.Platform.Communication.SendMessageAsync(chat,
            "Let’s agree on how often you want portfolio updates and which format to use.",
            $"director-reporting-intro:{source:N}", token);
        return await context.Platform.AskUserAsync(new(chat, null,
            "How often should I send portfolio updates? Weekly is my recommendation, with material decisions and blockers escalated separately. You can also enter 'Reports on request'.",
            [new("weekly", "Reports weekly", "One consolidated weekly portfolio update."),
             new("weekdays", "Reports weekdays", "A short update each weekday."),
             new("daily", "Reports daily", "A daily portfolio update."),
             new("milestones", "Reports at milestones", "Updates when material project milestones change.")],
            "weekly", $"director-reporting-cadence:{source:N}", ConversationMessageId: anchor.Id), token);
    }

    private async Task<bool> HandleReportingPreferenceAsync(CommunicationMessageReceivedEvent incoming, Guid chat,
        string text, AgentTurnStreamWriter stream, AgentRuntimeContext context, CancellationToken token)
    {
        var state = await context.Platform.ReadOperatingStateAsync<ReportingPreferences>(ReportingKey, token);
        var prior = state?.Payload ?? new ReportingPreferences(chat);
        text = ReportingAnswer(text);
        var cadence = ReportingCadence(text);
        var format = text.Trim().ToLowerInvariant() switch
        {
            "report format: messages" => "Messages", "report format: documents" => "Documents",
            "report format: both" => "Both", _ => null
        };
        if (text.Equals("change reporting preferences", StringComparison.OrdinalIgnoreCase))
        {
            await AskReportingCadenceAsync(chat, incoming.MessageId, context, token);
            await stream.CommitAsync("Choose the update cadence, then I’ll confirm the format and schedule.", token);
            return true;
        }
        if (text.Equals("report now", StringComparison.OrdinalIgnoreCase))
        {
            await PublishCeoReportAsync(prior with { Format = prior.Format ?? "Messages" }, $"on-request:{incoming.MessageId:N}", context, token);
            await stream.CommitAsync("The current portfolio update is recorded in this conversation.", token);
            return true;
        }
        var schedule = Regex.Match(text.Trim(), @"^(?:Report time:\s*)?(\d{2}:\d{2})\s+([A-Za-z_+\-/]+)$", RegexOptions.IgnoreCase);
        if (cadence is null && format is null && !(prior.Cadence is not null && prior.Format is not null && schedule.Success)) return false;
        var updated = prior with { ConversationId = chat };
        if (cadence is not null)
            updated = updated with { Cadence = cadence, Format = null, TimeZone = null, NextDue = null };
        else if (format is not null)
            updated = updated with { Format = format };
        else
        {
            if (!TimeOnly.TryParseExact(schedule.Groups[1].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                !TimeZoneInfo.TryFindSystemTimeZoneById(schedule.Groups[2].Value, out _))
            {
                await stream.CommitAsync("The schedule needs a valid time and timezone, for example: Report time: 09:00 America/Los_Angeles.", token);
                return true;
            }
            updated = updated with { LocalTime = schedule.Groups[1].Value, TimeZone = schedule.Groups[2].Value };
        }
        if (updated.Cadence is "Milestones" or "OnRequest") updated = updated with { TimeZone = "UTC" };
        if (updated.Format is not null && updated.TimeZone is not null)
            updated = updated with { NextDue = NextReportAt(updated, DateTimeOffset.UtcNow) };
        await SaveReportingAsync(updated, $"reporting-preference:{incoming.MessageId:N}", context, token);
        if (updated.Format is null)
            await context.Platform.AskUserAsync(new(chat, incoming.TurnId, "Which format should your portfolio updates use?",
                [new("messages", "Report format: messages", "Concise updates in Communications."),
                 new("documents", "Report format: documents", "Official report documents linked in Communications."),
                 new("both", "Report format: both", "A concise message and a detailed report document.")],
                "messages", $"director-report-format:{incoming.MessageId:N}"), token);
        else if (updated.TimeZone is null)
            await context.Platform.AskUserAsync(new(chat, incoming.TurnId,
                "Enter the update time and timezone, for example 'Report time: 09:00 America/Los_Angeles'. Weekly reports arrive on Friday.",
                [], string.Empty, $"director-report-time:{incoming.MessageId:N}"), token);
        else
        {
            await EnsureReportingTodoAsync(updated, context, token);
            await ContinueDirectorOnboardingAsync(context, token);
        }
        await stream.CommitAsync(updated.Format is null || updated.TimeZone is null
            ? "Your reporting preference is saved. Complete the remaining choice before recurring updates begin."
            : $"Portfolio updates will use {updated.Format.ToLowerInvariant()} on the {updated.Cadence} cadence" +
              (updated.NextDue is null ? "." : $", at {updated.LocalTime} {updated.TimeZone}."), token);
        return true;
    }

    internal static string ReportingAnswer(string text)
    {
        const string marker = "\nAnswer: ";
        var index = text.LastIndexOf(marker, StringComparison.Ordinal);
        return text.StartsWith("Decision: ", StringComparison.Ordinal) && index >= 0
            ? text[(index + marker.Length)..].Trim() : text.Trim();
    }

    internal static string? ReportingCadence(string text) => text.Trim().ToLowerInvariant() switch
    {
        "reports weekly" => "Weekly", "reports daily" => "Daily", "reports weekdays" => "Weekdays",
        "reports at milestones" => "Milestones", "reports on request" => "OnRequest", _ => null
    };

    internal static DateTimeOffset? NextReportAt(ReportingPreferences preferences, DateTimeOffset after)
    {
        if (preferences.TimeZone is null || preferences.Cadence is not ("Daily" or "Weekdays" or "Weekly")) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(preferences.TimeZone);
        var local = TimeZoneInfo.ConvertTime(after, zone).Date;
        var time = TimeOnly.ParseExact(preferences.LocalTime, "HH:mm", CultureInfo.InvariantCulture);
        for (var day = 0; day < 9; day++)
        {
            var date = local.AddDays(day);
            if (preferences.Cadence == "Weekly" && date.DayOfWeek != DayOfWeek.Friday ||
                preferences.Cadence == "Weekdays" && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, zone));
            if (utc > after) return utc;
        }
        throw new InvalidOperationException("No next report occurrence could be determined.");
    }

    private static async Task SaveReportingAsync(ReportingPreferences preferences, string key,
        AgentRuntimeContext context, CancellationToken token) =>
        _ = await new RevisionSafeProjectState(context.Platform).MergeAsync<ReportingPreferences>(ReportingKey,
            "video-game.director-reporting.v1", 1, _ => preferences, new Dictionary<string, string>(), key, token);

    private static async Task EnsureReportingTodoAsync(ReportingPreferences preferences, AgentRuntimeContext context, CancellationToken token)
    {
        if (preferences.NextDue is null) return;
        await context.Platform.PersonalTodo.AddAsync(new AddPersonalTodoItemRequest(
            "Send CEO portfolio update", "Read current project and subordinate evidence and deliver the confirmed report format.",
            WorkPriorities.Medium, preferences.NextDue, $"{ReportingTodo}:{preferences.NextDue:O}", CorrelationId: $"{ReportingTodo}:{preferences.NextDue:O}"), token);
    }

    private async Task<PersonalTodoResult> ExecuteReportingAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken token)
    {
        var state = await context.Platform.ReadOperatingStateAsync<ReportingPreferences>(ReportingKey, token);
        var preferences = state?.Payload;
        if (preferences?.NextDue is not { } due || preferences.Format is null || preferences.TimeZone is null)
            return PersonalTodoResult.Completed("No confirmed recurring reporting schedule is active.");
        if (item.CorrelationId != $"{ReportingTodo}:{due:O}") return PersonalTodoResult.Completed("This report occurrence was replaced by the current CEO schedule.");
        if (due > DateTimeOffset.UtcNow) return PersonalTodoResult.WaitingUntil(due, "Waiting for the confirmed CEO reporting time.");
        await PublishCeoReportAsync(preferences, $"scheduled:{due:O}", context, token);
        var next = NextReportAt(preferences, DateTimeOffset.UtcNow)!.Value;
        await SaveReportingAsync(preferences with { NextDue = next }, $"reporting-next:{due:O}", context, token);
        await EnsureReportingTodoAsync(preferences with { NextDue = next }, context, token);
        return PersonalTodoResult.Completed("Portfolio update delivered and the next occurrence is scheduled.");
    }

    private async Task PublishCeoReportAsync(ReportingPreferences preferences, string occurrence, AgentRuntimeContext context, CancellationToken token)
    {
        var key = "director-report:" + Digest(occurrence);
        var retained = await context.Platform.ReadOperatingStateAsync<CeoReportDraft>(key, token);
        if (retained is null)
        {
            var text = new StringBuilder("# Video game portfolio report\n\n");
            var index = await ReadPortfolioIndexAsync(context, token);
            foreach (var entry in index.Projects)
            {
                var state = (await ReadStateByKeyAsync(entry.StateKey, context, token)).State;
                text.AppendLine($"## {entry.WorkingTitle}\n\nPhase: {state.Phase}.");
                if (state.WorkstreamId is { } projectId)
                {
                    var project = await context.Platform.ReadWorkstreamAsync(new CSweet.WorkManagement.Contracts.ReadWorkstreamRequest(projectId), token);
                    text.AppendLine($"Project: {project.Name}. Status: {project.Status}.");
                }
                text.AppendLine(state.AcceptedVision is null ? "Pitch approval: pending." : "Pitch: approved exact revision retained.");
                var reports = state.SubordinateReports.GroupBy(x => x.ReporterOrganizationUserId)
                    .Select(group => group.MaxBy(x => x.ReportedAt)!).OrderByDescending(x => x.ReportedAt).Take(20).ToArray();
                if (reports.Length == 0) text.AppendLine("Delivery and creative evidence: no subordinate report received yet.");
                foreach (var report in reports)
                {
                    if (DateTimeOffset.UtcNow - report.ReportedAt > TimeSpan.FromDays(1)) text.AppendLine("Evidence freshness: this report is over 24 hours old; current delivery health is unconfirmed.");
                    text.AppendLine($"\n{report.ReporterDisplayName ?? "Team report"} ({report.ReportedAt:O}): {report.Summary}");
                    text.AppendLine("Risks: " + string.Join("; ", report.Risks));
                    text.AppendLine("Decisions: " + string.Join("; ", report.DecisionsNeeded));
                    text.AppendLine("Blockers: " + string.Join("; ", report.Blockers));
                }
                text.AppendLine("\nConstraints: " + string.Join("; ", state.ManagerPreferences.PlanningConstraints.Select(x => x.Text)));
                text.AppendLine("\nNext step: " + GameDirectorNextStep.Describe(state) + ".\n");
            }
            if (index.Projects.Count == 0) text.AppendLine("No game concepts or projects are recorded yet.");
            retained = await new RevisionSafeProjectState(context.Platform).MergeAsync<CeoReportDraft>(key,
                "video-game.director-report-draft.v1", 1, prior => prior ?? new(text.ToString()),
                new Dictionary<string, string>(), key + ":draft", token);
        }
        var markdown = retained.Payload.Markdown;
        string? link = null;
        if (preferences.Format is "Documents" or "Both")
        {
            var document = await context.Platform.Artifacts.CreateAsync(new("Game portfolio report", markdown,
                "video-game.portfolio-report.v1", key + ":document", OriginConversationId: preferences.ConversationId,
                StewardOrganizationUserId: Guid.Parse(context.Identity!.ManagerEmployeeId!)), token);
            link = $"[Official portfolio report](/organizations/{context.BusinessId}/documents?artifact={document.Id:D})";
        }
        await context.Platform.Communication.SendMessageAsync(preferences.ConversationId,
            preferences.Format == "Documents" ? link! : preferences.Format == "Both" ? "The portfolio report covers current phases, approval status, latest team evidence, risks and decisions.\n\n" + link : markdown, key + ":message", token);
    }

    private async Task ReportMilestoneChangesAsync(AgentRuntimeContext context, CancellationToken token)
    {
        var stored = await context.Platform.ReadOperatingStateAsync<ReportingPreferences>(ReportingKey, token);
        if (stored?.Payload is not { Cadence: "Milestones", Format: not null } preferences) return;
        var index = await ReadPortfolioIndexAsync(context, token);
        var facts = new List<string>();
        foreach (var entry in index.Projects.OrderBy(x => x.StateKey))
        {
            var current = (await ReadStateByKeyAsync(entry.StateKey, context, token)).State;
            var status = current.WorkstreamId is { } id
                ? (await context.Platform.ReadWorkstreamAsync(new ReadWorkstreamRequest(id), token)).Status : "Concept";
            facts.Add($"{entry.StateKey}:{current.Phase}:{status}:{current.AcceptedVision?.ArtifactRevisionId}");
        }
        var digest = Digest(string.Join("\n", facts));
        if (digest == preferences.LastMilestoneDigest || facts.Count == 0) return;
        await PublishCeoReportAsync(preferences, "milestone:" + digest, context, token);
        await SaveReportingAsync(preferences with { LastMilestoneDigest = digest }, "milestone-sent:" + digest, context, token);
    }

    private sealed record CeoReportDraft(string Markdown);
}
