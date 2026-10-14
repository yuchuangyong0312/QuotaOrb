using System.Text.Json;
using QuotaOrb.Core;
using QuotaOrb.Services;

var now = DateTimeOffset.Parse("2026-09-29T08:00:00Z");
var passed = 0;
void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
QuotaSnapshot Snapshot(double shortUsed = 10, double weekUsed = 20, bool? allowed = true, DateTimeOffset? fetched = null, DateTimeOffset? shortReset = null, DateTimeOffset? weekReset = null) =>
    new(fetched ?? now, "account-a", allowed, new() { ["codex"] = new("codex", null, "plus", new(shortUsed, 300, shortReset ?? now.AddHours(2)), new(weekUsed, 10080, weekReset ?? now.AddDays(2)), null, null, null) }, 2, null);
AccountRecord Account(QuotaSnapshot? snapshot = null) => new() { AuthState = "ready", Snapshot = snapshot ?? Snapshot() };
QuotaDecision Decide(AccountRecord a) => QuotaPolicy.Evaluate(a, "codex", now);
Check("positive confirmed windows recommend", Decide(Account()).CanRecommend);
Check("unknown permission is not confirmed availability", !Decide(Account(Snapshot(allowed:null))).CanRecommend);
Check("server denial overrides positive percentages", !Decide(Account(Snapshot(allowed:false))).CanRecommend);
Check("both exhausted waits for later deadline", Decide(Account(Snapshot(100,100))).ExpectedAt == now.AddDays(2));
Check("short exhausted waits for short deadline", Decide(Account(Snapshot(100,20))).ExpectedAt == now.AddHours(2));
Check("weekly exhausted blocks full short window", Decide(Account(Snapshot(0,100))).State == Availability.Exhausted);
var partial = Snapshot(0,100);
partial = partial with { Buckets = new() { ["codex"] = partial.Buckets["codex"] with { Primary = null } } };
Check("known weekly exhaustion is shown when 5h is missing", Decide(Account(partial)).State == Availability.Exhausted && Decide(Account(partial)).Label == "周额度已耗尽 · 5h 未知");
Check("partial exhaustion retains its next verification time without recommending", Decide(Account(partial)).ExpectedAt == now.AddDays(2) && !Decide(Account(partial)).CanRecommend);
partial = partial with { OrdinaryUsageAllowed = false, Buckets = new() { ["codex"] = partial.Buckets["codex"] with { Secondary = null } } };
Check("server denial survives missing windows", Decide(Account(partial)).State == Availability.Exhausted && Decide(Account(partial)).Label == "服务端限制使用");
Check("server denial survives a missing quota pool", Decide(Account(partial with { Buckets = new() })).State == Availability.Exhausted);
partial = partial with { OrdinaryUsageAllowed = true, Buckets = new() { ["codex"] = partial.Buckets["codex"] with { SpendControlReached = true } } };
Check("spend-control denial survives missing percentages", Decide(Account(partial)).State == Availability.Exhausted);
partial = partial with { Buckets = new() { ["codex"] = partial.Buckets["codex"] with { SpendControlReached = false } } };
Check("unknown windows are never treated as recovered", Decide(Account(partial)).State == Availability.Unknown);
Check("passed reset is pending, never auto refill", Decide(Account(Snapshot(100,20,shortReset:now.AddSeconds(-1)))).State == Availability.PendingReset);
Check("fresh server permission supports idle window with old timestamp", Decide(Account(Snapshot(0,20,shortReset:now.AddDays(-1)))).CanRecommend);
Check("crossing reset after snapshot invalidates old permission", Decide(Account(Snapshot(0,20,fetched:now.AddMinutes(-2),shortReset:now.AddMinutes(-1)))).State == Availability.PendingReset);
Check("3-day-old data is stale", Decide(Account(Snapshot(fetched:now.AddDays(-3)))).State == Availability.Stale);
Check("future timestamp fails closed after clock rollback", Decide(Account(Snapshot(fetched:now.AddMinutes(1)))).State == Availability.Stale);
var failed = Account(); failed.LastError = "network";
Check("failed sync cannot recommend old positive data", !Decide(failed).CanRecommend);
var unlinked = Account(); unlinked.AuthState = "login_required";
Check("expired auth excludes recommendation", Decide(unlinked).State == Availability.LoginRequired);
unlinked.AuthState="unexpected";Check("unknown auth state fails closed",!Decide(unlinked).CanRecommend);
var disabled = Account(); disabled.Enabled = false;
Check("disabled excludes recommendation", Decide(disabled).State == Availability.Disabled);
Check("unrelated pool is not substituted", QuotaPolicy.Evaluate(Account(), "codex_other", now).State == Availability.Unknown);
using var nullDoc = JsonDocument.Parse("{\"rateLimits\":{\"primary\":null,\"secondary\":null},\"rateLimitResetCredits\":null}");
var nullParsed = QuotaParser.Parse(nullDoc.RootElement, now);
Check("missing credit count stays unknown", nullParsed.ResetCount is null);
Check("missing windows stay unknown", !Decide(Account(nullParsed)).CanRecommend);
using var swapped = JsonDocument.Parse("""
{"ordinaryUsageAllowed":true,"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":21,"windowDurationMins":10080,"resetsAt":1790834400},"secondary":{"usedPercent":7,"windowDurationMins":300,"resetsAt":1790834400}}},"rateLimitResetCredits":{"availableCount":8,"credits":[{"id":"one","status":"available","expiresAt":null}]}}
""");
var parsed = QuotaParser.Parse(swapped.RootElement, now);
Check("window semantics use duration, not order", parsed.Buckets["codex"].ForDuration(300)?.Remaining == 93);
Check("total credits not inferred from detail count", parsed.ResetCount == 8 && parsed.ResetCredits?.Count == 1);
using var malformed = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":-2,\"windowDurationMins\":300,\"resetsAt\":9223372036854775807}}}");
var invalid = QuotaParser.Parse(malformed.RootElement, now).Buckets["codex"].Primary;
Check("negative usage never becomes full balance", invalid?.Remaining is null);
Check("invalid unix timestamp is unknown", invalid?.ResetsAt is null);
Check("over-100 usage clamps remaining to zero", new QuotaWindow(130,300,null).Remaining == 0);
Check("precision is not rounded into exhaustion", new QuotaWindow(99.99,300,null).Remaining > 0);
var holiday = Account(Snapshot(fetched:now.AddDays(-3),shortReset:now.AddDays(-3).AddHours(2)));
QuotaPolicy.AcceptSnapshot(holiday, Snapshot());
Check("holiday records gap without inventing 14 resets", holiday.Gaps.Count == 1 && holiday.History.Count == 1 && holiday.History[0].Kind == "间隔后发现窗口更新");
QuotaPolicy.AcceptSnapshot(holiday, Snapshot());
Check("repeated snapshot does not duplicate history", holiday.History.Count == 1);
Check("resync restores fresh display", Decide(holiday).CanRecommend);
var decrease = Account(Snapshot(60,20,fetched:now.AddMinutes(-3)));
QuotaPolicy.AcceptSnapshot(decrease, Snapshot(0,20));
Check("percentage decrease alone does not prove reset", decrease.History.Count == 0);
var restored = JsonSerializer.Deserialize<AccountRecord>(JsonSerializer.Serialize(holiday))!;
Check("history survives serialization", restored.History.Count == 1 && restored.Snapshot?.Buckets["codex"].ForDuration(300)?.Remaining == 90);
Check("foreground waits the full 15 minutes", !RefreshPolicy.IsDue(now, now.AddMinutes(15).AddSeconds(-1), true) && RefreshPolicy.IsDue(now, now.AddMinutes(15), true));
Check("background waits the full 30 minutes", !RefreshPolicy.IsDue(now, now.AddMinutes(30).AddSeconds(-1), false) && RefreshPolicy.IsDue(now, now.AddMinutes(30), false));
Check("reopening a recently checked panel does not poll", Enumerable.Range(1, 14).All(minutes => !RefreshPolicy.IsDue(now, now.AddMinutes(minutes), true)));
Check("opening after 20 background minutes makes polling due", !RefreshPolicy.IsDue(now, now.AddMinutes(20), false) && RefreshPolicy.IsDue(now, now.AddMinutes(20), true));
Check("clock rollback and initial startup recheck", RefreshPolicy.IsDue(now, now.AddSeconds(-1), false) && RefreshPolicy.IsDue(DateTimeOffset.MinValue, now, false));
Check("background snapshot stays usable until next cycle", Decide(Account(Snapshot(fetched:now.AddMinutes(-30)))).CanRecommend);
Check("snapshot beyond background cycle and query grace is stale", Decide(Account(Snapshot(fetched:now.AddMinutes(-36)))).State == Availability.Stale);
var oldSettings = JsonSerializer.Deserialize<AppSettings>("""{"ForegroundMinutes":3,"BackgroundMinutes":12,"AutoRefresh":false,"Notifications":false,"LimitId":"codex"}""")!;
Check("legacy intervals cannot override new cadence and other settings survive", !oldSettings.AutoRefresh && !oldSettings.Notifications && oldSettings.LimitId == "codex" && !JsonSerializer.Serialize(oldSettings).Contains("Minutes"));
var discoveryRoot = Path.Combine(Path.GetTempPath(), "quota-orb-discovery-" + Guid.NewGuid().ToString("N"));
try
{
    var installedBin = Path.Combine(discoveryRoot, "OpenAI", "Codex", "bin");
    var cliBin = Path.Combine(discoveryRoot, "CLI with spaces");
    Directory.CreateDirectory(cliBin);
    var cliExecutable = Path.Combine(cliBin, "codex.exe");
    File.WriteAllText(cliExecutable, "test file only");
    string? path = null;
    var installation = new CodexInstallation([installedBin], () => path);
    Check("missing installation does not invent a bundled path", installation.FindExecutable() is null);
    var missingMessage = "";
    try { installation.RequireExecutable(); } catch (FileNotFoundException error) { missingMessage = error.Message; }
    Check("missing installation tells user to install Codex", missingMessage == CodexInstallation.MissingMessage);
    path = "\"" + cliBin + "\"";
    Check("quoted PATH with spaces finds an installed CLI", installation.FindExecutable() == cliExecutable);
    var olderExecutable = Path.Combine(installedBin, "old-version", "codex.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(olderExecutable)!);
    File.WriteAllText(olderExecutable, "test file only");
    File.SetLastWriteTimeUtc(olderExecutable, DateTime.UtcNow.AddDays(-1));
    Check("desktop installed after startup is found before PATH", installation.FindExecutable() == olderExecutable);
    var newerExecutable = Path.Combine(installedBin, "new-version", "codex.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(newerExecutable)!);
    File.WriteAllText(newerExecutable, "test file only");
    Check("Codex update is discovered without restarting app", installation.FindExecutable() == newerExecutable);
    File.Delete(newerExecutable);
    Check("removed version path is not cached", installation.FindExecutable() == olderExecutable);
    File.Delete(olderExecutable);
    Check("uninstalled desktop falls back to installed CLI", installation.FindExecutable() == cliExecutable);
    path = "." + Path.PathSeparator + "\0invalid";
    Check("relative or invalid PATH entries cannot start a stray executable", installation.FindExecutable() is null);
}
finally
{
    if (!Path.GetFullPath(discoveryRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(discoveryRoot).StartsWith("quota-orb-discovery-", StringComparison.Ordinal))
        throw new InvalidOperationException("Discovery test cleanup is outside its temporary directory.");
    Directory.Delete(discoveryRoot, recursive: true);
}
Console.WriteLine($"{passed} meaningful quota and installation checks passed.");
