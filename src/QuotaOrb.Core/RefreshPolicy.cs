namespace QuotaOrb.Core;

public static class RefreshPolicy
{
    public const int ForegroundMinutes = 15;
    public const int BackgroundMinutes = 30;

    public static bool IsDue(DateTimeOffset lastPoll, DateTimeOffset now, bool expanded) =>
        now < lastPoll || now - lastPoll >= TimeSpan.FromMinutes(expanded ? ForegroundMinutes : BackgroundMinutes);
}
