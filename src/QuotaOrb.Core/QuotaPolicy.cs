namespace QuotaOrb.Core;

public static class QuotaPolicy
{
    // Cover the background interval plus time for a serialized round of account queries.
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(RefreshPolicy.BackgroundMinutes + 5);

    public static QuotaDecision Evaluate(AccountRecord account, string limitId, DateTimeOffset now)
    {
        if (!account.Enabled) return new(Availability.Disabled, "已停用", null, null);
        if (account.AuthState != "ready") return new(Availability.LoginRequired, "需要登录授权", null, null);
        if (account.LastError is not null) return new(Availability.Error, "查询失败 · 数据待核验", null, null);
        if (account.Snapshot is not { } snapshot) return new(Availability.Unknown, "尚未查询", null, null);
        var age = now - snapshot.FetchedAt;
        if (age < TimeSpan.Zero || age > Freshness) return new(Availability.Stale, "数据已过期", null, null);
        if (!snapshot.Buckets.TryGetValue(limitId, out var bucket))
            return snapshot.OrdinaryUsageAllowed == false ? new(Availability.Exhausted, "服务端限制使用", null, null) : new(Availability.Unknown, "该额度池暂无数据", null, null);
        var shortWindow = bucket.ForDuration(300);
        var weekly = bucket.ForDuration(10080);
        var windows = new[] { shortWindow, weekly }.OfType<QuotaWindow>().ToArray();
        var exhausted = windows.Where(w => w.Remaining is <= 0).ToArray();
        DateTimeOffset? readyAt = exhausted.Length > 0 && exhausted.All(w => w.ResetsAt.HasValue) ? exhausted.Max(w => w.ResetsAt) : null;
        if (exhausted.Length == 0 && (!string.IsNullOrEmpty(bucket.ReachedType) || bucket.SpendControlReached == true || bucket.IndividualRemaining is <= 0 || snapshot.OrdinaryUsageAllowed == false))
            return new(Availability.Exhausted, "服务端限制使用", null, null);
        // A passed deadline is a reason to re-query, never evidence of recovery.
        if (windows.Any(w => w.ResetsAt is { } reset && reset <= now && (reset > snapshot.FetchedAt || w.Remaining is <= 0)))
            return new(Availability.PendingReset, "刷新时间已到 · 待核验", readyAt, null);
        if (exhausted.Length > 0)
        {
            var label = shortWindow?.Remaining <= 0 && weekly?.Remaining <= 0 ? "5h 与周额度已耗尽" : weekly?.Remaining <= 0 ? "周额度已耗尽" : "5h 额度已耗尽";
            if (shortWindow?.Remaining is null) label += " · 5h 未知";
            if (weekly?.Remaining is null) label += " · 周额度未知";
            return new(Availability.Exhausted, label, readyAt, null);
        }
        if (shortWindow?.Remaining is null || weekly?.Remaining is null) return new(Availability.Unknown, "窗口信息不完整", null, null);
        // Newer protocol explicitly forbids inferring ordinary-usage recovery when permission is unknown.
        if (snapshot.OrdinaryUsageAllowed != true)
            return new(Availability.Unknown, "余额有余 · 可用性未确认", null, null);
        return new(Availability.Available, "额度可用", now, Math.Min(shortWindow.Remaining.Value, weekly.Remaining.Value));
    }

    public static void AcceptSnapshot(AccountRecord account, QuotaSnapshot next)
    {
        var previous = account.Snapshot;
        if (previous is not null)
        {
            var elapsed = next.FetchedAt - previous.FetchedAt;
            if (elapsed > TimeSpan.FromMinutes(30))
                account.Gaps.Add(new(previous.FetchedAt, next.FetchedAt));
            foreach (var (id, bucket) in next.Buckets)
            {
                if (!previous.Buckets.TryGetValue(id, out var oldBucket)) continue;
                if (oldBucket.Plan is not null && bucket.Plan is not null && oldBucket.Plan != bucket.Plan) continue;
                var oldWindow = oldBucket.ForDuration(300);
                var newWindow = bucket.ForDuration(300);
                if (oldWindow?.ResetsAt is not { } deadline || newWindow?.ResetsAt is not { } nextDeadline) continue;
                // Record only observed transitions. A monitoring gap gets its own label, never five-hour backfill.
                if (deadline <= next.FetchedAt && deadline > previous.FetchedAt && nextDeadline > deadline && newWindow.Remaining is not null)
                {
                    var key = $"{id}:300:{deadline.ToUnixTimeSeconds()}";
                    if (account.History.All(h => h.Key != key))
                        account.History.Add(new(key, id, deadline, next.FetchedAt, elapsed > TimeSpan.FromMinutes(30) ? "间隔后发现窗口更新" : "观察到窗口更新"));
                }
            }
        }
        account.History = account.History.OrderByDescending(h => h.ObservedAt).Take(100).ToList();
        account.Gaps = account.Gaps.OrderByDescending(g => g.To).Take(20).ToList();
        account.Snapshot = next;
        account.LastSuccessUtc = next.FetchedAt;
        account.LastError = null;
        account.AuthState = "ready";
    }
}
