namespace costats.Core.Pulse;

/// <summary>
/// Codex's manual "reset my limits early" credits. Each credit expires roughly a month
/// after it's granted; <see cref="NextExpiresAt"/> is the soonest upcoming expiration and
/// <see cref="Expirations"/> lists every upcoming expiration in ascending order.
/// </summary>
public sealed record CodexResetCredits(
    int AvailableCount,
    DateTimeOffset? NextExpiresAt,
    IReadOnlyList<DateTimeOffset>? Expirations = null);
