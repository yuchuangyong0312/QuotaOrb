using System.Text.Json.Serialization;

namespace QuotaOrb.Core;

public sealed class AccountRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新账号";
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "unknown";
    public string Notes { get; set; } = "";
    public string? ProtectedNotes { get; set; }
    [JsonIgnore] public bool NotesUnavailable { get; set; }
    public string? ProtectedPassword { get; set; }
    public DateOnly? RechargeDate { get; set; }
    public DateOnly? SubscriptionDate { get; set; }
    public TimeOnly? RechargeTime { get; set; }
    public TimeOnly? SubscriptionTime { get; set; }
    public string? ProtectedTwoFactorUrl { get; set; }
    public string? ProtectedTwoFactorKey { get; set; }
    public bool LoginInfoInitialized { get; set; }
    public bool Enabled { get; set; } = true;
    public string AuthState { get; set; } = "unlinked";
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
    public QuotaSnapshot? Snapshot { get; set; }
    public List<ResetObservation> History { get; set; } = [];
    public List<MonitoringGap> Gaps { get; set; } = [];
}

public sealed class AppSettings
{
    public bool Notifications { get; set; } = true;
    public bool AutoRefresh { get; set; } = true;
    public string LimitId { get; set; } = "codex";
}

public sealed class AppData
{
    public int Version { get; set; } = 3;
    public bool NotesCleanupPending { get; set; }
    public List<AccountRecord> Accounts { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
    public DateTimeOffset? LastRunningUtc { get; set; }
}

public sealed record QuotaWindow(double? UsedPercent, long? DurationMinutes, DateTimeOffset? ResetsAt)
{
    [JsonIgnore] public double? Remaining => UsedPercent is { } n && double.IsFinite(n) && n >= 0 ? Math.Clamp(100 - n, 0, 100) : null;
}

public sealed record QuotaBucket(string Id, string? Name, string? Plan, QuotaWindow? Primary, QuotaWindow? Secondary,
    string? ReachedType, bool? SpendControlReached, int? IndividualRemaining)
{
    [JsonIgnore] public IEnumerable<QuotaWindow> Windows => new[] { Primary, Secondary }.OfType<QuotaWindow>();
    public QuotaWindow? ForDuration(long minutes) => Windows.SingleOrDefaultSafe(w => w.DurationMinutes == minutes);
}

public sealed record ResetCredit(string Id, string? Status, string? ResetType, DateTimeOffset? ExpiresAt, string? Title);
public sealed record QuotaSnapshot(DateTimeOffset FetchedAt, string? ServerAccountId, bool? OrdinaryUsageAllowed,
    Dictionary<string, QuotaBucket> Buckets, int? ResetCount, List<ResetCredit>? ResetCredits);
public sealed record ResetObservation(string Key, string LimitId, DateTimeOffset ScheduledAt, DateTimeOffset ObservedAt, string Kind);
public sealed record MonitoringGap(DateTimeOffset From, DateTimeOffset To);
public enum Availability { Available, Exhausted, PendingReset, Stale, Unknown, LoginRequired, Disabled, Error }
public sealed record QuotaDecision(Availability State, string Label, DateTimeOffset? ExpectedAt, double? Score)
{
    public bool CanRecommend => State == Availability.Available;
}

internal static class EnumerableExtensions
{
    public static T? SingleOrDefaultSafe<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        var matches = source.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
