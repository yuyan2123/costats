using costats.Application.Pulse;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CodexLogSource : ISignalSource
{
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromHours(3);
    private static readonly TimeSpan DefaultWeekDuration = TimeSpan.FromDays(7);

    private readonly UsageLogScanner _scanner = new();
    private readonly CodexOAuthUsageFetcher _oauthFetcher = new();
    private readonly CodexResetCreditsFetcher _resetCreditsFetcher = new();
    private readonly ExpenseAnalyzer _expenseAnalyzer = new();

    public ProviderProfile Profile => ProviderCatalog.Codex;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // OAuth and reset-credits are network calls - run in parallel with file I/O
        var oauthTask = _oauthFetcher.FetchAsync(cancellationToken);
        var resetCreditsTask = _resetCreditsFetcher.FetchAsync(cancellationToken);

        // Log scan and expense analysis both read the same files - run sequentially to halve peak memory
        var logResult = await _scanner.ScanCodexAsync(cancellationToken).ConfigureAwait(false);
        var consumption = await SafeAnalyzeExpenseAsync(cancellationToken).ConfigureAwait(false);

        var oauthResult = await oauthTask.ConfigureAwait(false);
        var resetCredits = await resetCreditsTask.ConfigureAwait(false);

        if (oauthResult is null && logResult.SessionTokens == 0 && logResult.WeekTokens == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: "No Codex usage data available",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.LocalLog);
        }

        // Prefer OAuth data for percentages
        var sessionUsedPercent = oauthResult?.PrimaryUsedPercent;
        var weeklyUsedPercent = oauthResult?.SecondaryUsedPercent;

        // OpenAI removed the 5-hour session limit for some plans: the OAuth call succeeds but
        // reports no session window. Treat that as "session quota not applicable" (rendered as
        // N/A) rather than a log-based estimate that would look like a real session limit.
        var sessionWindowUnavailable = oauthResult is not null
            && oauthResult.PrimaryUsedPercent is null
            && oauthResult.PrimaryWindowSeconds is null;

        // Get window durations from API or use defaults
        var sessionDuration = oauthResult?.PrimaryWindowSeconds is not null
            ? TimeSpan.FromSeconds(oauthResult.PrimaryWindowSeconds.Value)
            : DefaultSessionDuration;

        var weekDuration = oauthResult?.SecondaryWindowSeconds is not null
            ? TimeSpan.FromSeconds(oauthResult.SecondaryWindowSeconds.Value)
            : DefaultWeekDuration;

        var sessionResetsAt = oauthResult?.PrimaryResetsAt ?? CalculateSessionReset(logResult.SessionStart, now, sessionDuration);
        var weeklyResetsAt = oauthResult?.SecondaryResetsAt ?? CalculateWeeklyReset(now);

        QuotaWindow? sessionWindow = sessionWindowUnavailable
            ? null
            : new QuotaWindow(sessionDuration, sessionResetsAt);
        var weekWindow = new QuotaWindow(weekDuration, weeklyResetsAt);

        // Use percentage data directly when available
        long? sessionUsed;
        long? sessionLimit;
        long? weekUsed;
        long? weekLimit;

        if (sessionWindowUnavailable)
        {
            // No session quota to report; the widget shows this as N/A.
            sessionUsed = null;
            sessionLimit = null;
        }
        else if (sessionUsedPercent is not null)
        {
            // Store percentage directly: used=percentage, limit=100
            sessionUsed = (long)Math.Round(sessionUsedPercent.Value);
            sessionLimit = 100;
        }
        else
        {
            sessionUsed = logResult.SessionTokens > 0 ? logResult.SessionTokens : null;
            sessionLimit = null;
        }

        if (weeklyUsedPercent is not null)
        {
            weekUsed = (long)Math.Round(weeklyUsedPercent.Value);
            weekLimit = 100;
        }
        else
        {
            weekUsed = logResult.WeekTokens > 0 ? logResult.WeekTokens : null;
            weekLimit = null;
        }

        // Build prepaid balance bucket when credits are available
        MonetaryBucket? spendingBucket = null;
        if (oauthResult is { HasCredits: true, CreditBalance: not null } && oauthResult.CreditBalance.Value > 0)
        {
            spendingBucket = MonetaryBucket.ForPrepaidBalance((decimal)oauthResult.CreditBalance.Value);
        }

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: oauthResult?.FetchedAt ?? logResult.LatestTimestamp ?? now,
            SessionUsed: sessionUsed,
            SessionLimit: sessionLimit,
            WeekUsed: weekUsed,
            WeekLimit: weekLimit,
            SpendingBucket: spendingBucket,
            Consumption: consumption,
            SessionWindow: sessionWindow,
            WeekWindow: weekWindow,
            ResetCredits: resetCredits);

        var planText = FormatPlanText(oauthResult?.PlanType);
        var statusSummary = oauthResult is not null
            ? $"Updated {FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : $"Updated {FormatRelativeTime(logResult.LatestTimestamp ?? now, now)}";

        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        var source = oauthResult is not null ? ReadingSource.Api : ReadingSource.LocalLog;

        return new ProviderReading(
            Usage: usage,
            Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, planText, "OAuth"),
            StatusSummary: statusSummary,
            CapturedAt: usage.CapturedAt,
            Confidence: confidence,
            Source: source);
    }

    private static string FormatPlanText(string? planType)
    {
        if (string.IsNullOrEmpty(planType))
        {
            return "Pro";
        }

        // Convert "pro" to "Pro", "plus" to "Plus", etc.
        return char.ToUpper(planType[0]) + planType[1..].ToLower();
    }

    private static DateTimeOffset? CalculateSessionReset(DateTimeOffset? sessionStart, DateTimeOffset now, TimeSpan sessionDuration)
    {
        if (sessionStart is null)
        {
            return now + sessionDuration;
        }

        var elapsed = now - sessionStart.Value;
        if (elapsed >= sessionDuration)
        {
            return now + sessionDuration;
        }

        return sessionStart.Value + sessionDuration;
    }

    private static DateTimeOffset CalculateWeeklyReset(DateTimeOffset now)
    {
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0 && now.TimeOfDay > TimeSpan.Zero)
        {
            daysUntilMonday = 7;
        }

        var nextMonday = now.Date.AddDays(daysUntilMonday);
        return new DateTimeOffset(nextMonday, TimeSpan.Zero);
    }

    private async Task<ConsumptionDigest?> SafeAnalyzeExpenseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _expenseAnalyzer.AnalyzeCodexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cost analysis failure should not break usage display
            return null;
        }
    }
}
