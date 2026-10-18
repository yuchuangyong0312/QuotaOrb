global using QuotaOrb.Core;
global using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaOrb.Services;

if (args.Contains("app-server")) { await AuthorizationChecks.RunRpcHelperAsync(); return; }

if (args.Contains("--inspect-local-read-only"))
{
    try { await LocalCompatibilityChecks.RunAsync(); }
    catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--inspect-isolated-protocol"))
{
    try { await LocalCompatibilityChecks.CheckIsolatedProtocolAsync(); }
    catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--inspect-live-access-read-only"))
{
    try { await LocalCompatibilityChecks.CheckLiveAccessReadOnlyAsync(); }
    catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); Environment.ExitCode = 1; }
    return;
}

var root = Path.Combine(Path.GetTempPath(), "quota-orb-switch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
async Task<bool> Reject(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or CryptographicException) { return true; } }
Fixture New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));

await CleanupChecks.RunAsync(root, Check);

foreach (var wrapped in new[] { false, true })
{
    var f = New(); var oldLogin = Fixture.Login("b", "old-desktop-copy");
    var payload = wrapped ? JsonSerializer.SerializeToUtf8Bytes(new { QuotaOrbSnapshotVersion = 2, Login = oldLogin, PendingEnrollment = false }) : oldLogin;
    var encrypted = ProtectedData.Protect(payload, Encoding.UTF8.GetBytes("QuotaOrb/desktop-login/v1/account/" + f.B.Id), DataProtectionScope.CurrentUser);
    var cachePath = Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin");
    File.WriteAllBytes(cachePath, encrypted);
    Check("an old desktop cache is not advertised as completed switching authorization: " + wrapped,
        !f.Switcher.GetMessage(f.B).Contains("已保存切换授权") && f.Switcher.GetMessage(f.B).Contains("授权切换登录"));
    Check("legacy cache disables the switch eligibility used by the UI: " + wrapped,
        !f.Switcher.HasAuthorized(f.B) && f.Switcher.GetAuthorizationState(f.B) == DesktopAuthorizationState.Legacy);
    Check("a legacy cache cannot start a switch or touch the active login: " + wrapped,
        await Reject(() => f.Switch()) && f.Probe.Calls == 0 && f.Runtime.Launches == 0 && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
    Check("legacy status inspection preserves the existing encrypted login: " + wrapped,
        File.ReadAllBytes(cachePath).SequenceEqual(encrypted) && f.Vault.Load(f.B.Id).SequenceEqual(oldLogin));
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "b"));
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
    await switcher.SwitchAsync(f.B, [f.A, f.B], "codex", default);
    Check("official enrollment upgrades an old cache and permits switching: " + wrapped,
        result.Success && f.Runtime.Launches == 1 && DesktopLogin.Inspect(f.Current()).AccountId == "account-b");
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe);
    Check("completed enrollment stays authorized after the first launch and restart: " + wrapped, restarted.HasAuthorized(f.B));
}

{
    var f = New(); var previous = Fixture.Login("b", "previous-preview-enrollment");
    var payload = JsonSerializer.SerializeToUtf8Bytes(new { QuotaOrbSnapshotVersion = 2, Login = previous, PendingEnrollment = true });
    var encrypted = ProtectedData.Protect(payload, Encoding.UTF8.GetBytes("QuotaOrb/desktop-login/v1/account/" + f.B.Id), DataProtectionScope.CurrentUser);
    var path = Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin");
    File.WriteAllBytes(path, encrypted);
    Check("previous preview's confirmed enrollment is recognized without rewriting it",
        f.Switcher.HasAuthorized(f.B) && File.ReadAllBytes(path).SequenceEqual(encrypted));
    f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("b", "rotated-after-upgrade")); return Task.FromResult(Fixture.Quota("b")); };
    await f.Switch();
    f.Runtime.Running = false; f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("a"));
    await f.Switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default);
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe);
    Check("authorization survives token rotation, leaving the account and tool restart",
        restarted.HasAuthorized(f.B) && f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "rotated-after-upgrade")));
}
{
    var f = New(); f.Vault.Remove(f.A.Id);
    await f.Switch();
    Check("automatically preserving an outgoing desktop session does not grant independent authorization",
        f.Switcher.HasSaved(f.A) && !f.Switcher.HasAuthorized(f.A) && f.Switcher.GetAuthorizationState(f.A) == DesktopAuthorizationState.Legacy
        && !f.Switcher.GetMessage(f.A).Contains("已保存切换授权"));
}
{
    var f = New(); var path = Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin");
    File.WriteAllBytes(path, [1, 2, 3]);
    Check("an unreadable cache never advertises authorization or enables switching",
        !f.Switcher.HasAuthorized(f.B) && f.Switcher.GetAuthorizationState(f.B) == DesktopAuthorizationState.Unavailable
        && !f.Switcher.GetMessage(f.B).Contains("已保存切换授权"));
    f.Vault.Save(f.B.Id, Fixture.Login("a"), authorizedForSwitching: true);
    Check("an authorized marker for a different identity cannot enable switching",
        !f.Switcher.HasAuthorized(f.B) && await Reject(() => f.Switch()) && f.Runtime.Launches == 0 && f.Current().SequenceEqual(f.Original));
}
{
    var f = New(); f.Vault.Save(f.B.Id, Fixture.Login("b", "legacy"));
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "a"));
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe);
    Check("a failed new authorization cannot promote a legacy desktop copy",
        !result.Success && !switcher.HasAuthorized(f.B) && restarted.GetAuthorizationState(f.B) == DesktopAuthorizationState.Legacy);
}

{
    var f = New(); await f.Switcher.SaveCurrentAsync(f.A, default);
    Check("saving current desktop login never invokes the network probe", f.Probe.Calls == 0 && f.Switcher.HasSaved(f.A));
    Check("manual desktop capture does not claim independent authorization", !f.Switcher.HasAuthorized(f.A));
    var encrypted = File.ReadAllBytes(Path.Combine(f.Root, "data", "desktop-logins", f.A.Id + ".bin"));
    Check("desktop credential cache is encrypted", !Encoding.UTF8.GetString(encrypted).Contains("SYNTHETIC_REFRESH") && !encrypted.SequenceEqual(f.Original));
    var wrongBefore = f.Vault.Load(f.B.Id);
    Check("saving the wrong account is rejected without replacing its cache", await Reject(() => f.Switcher.SaveCurrentAsync(f.B, default)) && f.Vault.Load(f.B.Id).SequenceEqual(wrongBefore));
    f.Vault.Save(f.B.Id, Fixture.Login("b"));
    File.Copy(Path.Combine(f.Root, "data", "desktop-logins", f.A.Id + ".bin"), Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin"), true);
    Check("encrypted snapshots cannot be swapped between account ids", await Reject(() => { f.Vault.Load(f.B.Id); return Task.CompletedTask; }));
}
{
    var malformed = Encoding.UTF8.GetBytes("{\"auth_mode\":\"api\",\"OPENAI_API_KEY\":\"synthetic\"}");
    Check("API credentials are not accepted as ChatGPT desktop sessions", await Reject(() => { DesktopLogin.Inspect(malformed); return Task.CompletedTask; }));
    var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture.Login("a")).Replace("{\"auth_mode\"", "{\"auth_mode\":\"chatgpt\",\"auth_mode\""));
    Check("ambiguous duplicate JSON properties are rejected", await Reject(() => { DesktopLogin.Inspect(duplicate); return Task.CompletedTask; }));
    Check("oversized login files are rejected", await Reject(() => { DesktopLogin.Inspect(new byte[DesktopLogin.MaxBytes + 1]); return Task.CompletedTask; }));
}
{
    var f = New(); f.Runtime.Running = true;
    Check("running Codex blocks every auth-file write and launch", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending && f.Probe.Calls == 0 && f.Runtime.Launches == 0);
}
{
    var f = New(); f.B.Enabled = false;
    Check("a disabled account cannot start a switch", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending && f.Probe.Calls == 0);
}
{
    var f = New();
    f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("b", "rotated")); return Task.FromResult(Fixture.Quota("b")); };
    await f.Switch();
    Check("verified target launches exactly once and completes the journal", f.Runtime.Launches == 1 && !f.Vault.HasPending && DesktopLogin.Inspect(f.Current()).AccountId == "account-b");
    Check("switching away saves the latest outgoing credentials", f.Vault.Load(f.A.Id).SequenceEqual(f.Original));
    Check("rotated target credentials replace its cached copy", f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "rotated")));
    Check("configuration and chat marker remain unchanged", File.ReadAllText(Path.Combine(f.Runtime.Home, "config.toml")) == Fixture.Config && File.ReadAllText(Path.Combine(f.Runtime.Home, "chat-marker.txt")) == "untouched");
    f.Runtime.Running = false; f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("a"));
    await f.Switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default);
    Check("synthetic A to B to A preserves each credential independently", f.Current().SequenceEqual(f.Original) && f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "rotated")));
}
{
    var f = New(); f.Probe.Action = (_, _, _) => throw new IOException("SYNTHETIC_SECRET_BACKEND_PAYLOAD");
    Check("verification failure restores original credentials", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending && f.Runtime.Launches == 0);
    Check("raw backend errors never enter user-visible status", !f.Switcher.GetMessage(f.B).Contains("SYNTHETIC_SECRET"));
}
{
    var f = New(); f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("b", "renewed")); return Task.FromResult(Fixture.Quota("b", 100)); };
    await f.Switch();
    Check("exhausted target can log in to manage reset cards", f.Runtime.Launches == 1 && !f.Vault.HasPending && DesktopLogin.Inspect(f.Current()).AccountId == "account-b");
    Check("exhausted target keeps credentials renewed during verification", f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "renewed")) && new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe).HasAuthorized(f.B));
}
{
    var f = New(); f.Runtime.FailLaunch = true;
    f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("b", "renewed")); return Task.FromResult(Fixture.Quota("b", 100)); };
    Check("exhausted target still rolls back on an actual launch failure", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && f.Runtime.Launches == 1 && !f.Vault.HasPending);
    Check("rollback retains a target token renewed during verification", f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "renewed")));
    Check("rollback retains proof of the target's prior authorization", new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe).HasAuthorized(f.B));
}
{
    var f = New(); f.Runtime.FailLaunch = true;
    Check("launch failure restores prior login with no pending journal", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
}
{
    var f = New(); f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("c"));
    Check("a different server identity cannot reach desktop launch", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && f.Runtime.Launches == 0);
    f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("b") with { Buckets = new() });
    Check("empty official quota results do not commit a switch", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original));
    f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("b"));
    Check("a missing selected quota pool is reported without launching", await Reject(() => f.Switcher.SwitchAsync(f.B, [f.A, f.B], "missing-pool", default)) && f.Current().SequenceEqual(f.Original) && f.Runtime.Launches == 0);
}
foreach (var restriction in new[] { "spend-control", "individual", "reached-type", "5h", "weekly", "ordinary-usage", "no-reset-cards" })
{
    var f = New(); var quota = Fixture.Quota("b"); var bucket = quota.Buckets["codex"];
    quota.Buckets["codex"] = restriction switch
    {
        "spend-control" => bucket with { SpendControlReached = true },
        "individual" => bucket with { IndividualRemaining = 0 },
        "reached-type" => bucket with { ReachedType = "synthetic-limit" },
        "5h" or "no-reset-cards" => bucket with { Primary = bucket.Primary! with { UsedPercent = 100 } },
        "weekly" => bucket with { Secondary = bucket.Secondary! with { UsedPercent = 100 } },
        _ => bucket
    };
    if (restriction == "ordinary-usage") quota = quota with { OrdinaryUsageAllowed = false };
    if (restriction == "no-reset-cards") quota = quota with { ResetCount = 0, ResetCredits = [] };
    f.Probe.Action = (_, _, _) => Task.FromResult(quota);
    await f.Switch();
    Check("usage limit does not block an authenticated desktop login: " + restriction, f.Probe.Calls == 1 && f.Runtime.Launches == 1 && DesktopLogin.Inspect(f.Current()).AccountId == "account-b" && !f.Vault.HasPending);
}
{
    var f = New(); f.Probe.Action = (_, _, _) => throw new OperationCanceledException();
    Check("cancelled verification rolls back before returning", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
}
{
    var f = New(); var hold = new TaskCompletionSource<QuotaSnapshot>();
    f.Probe.Action = (_, _, _) => hold.Task;
    var first = f.Switch();
    Check("double-clicks cannot start overlapping switches", await Reject(() => f.Switch()));
    Check("saving cannot run during a switch", await Reject(() => f.Switcher.SaveCurrentAsync(f.A, default)));
    hold.SetResult(Fixture.Quota("b")); await first;
}
{
    var f = New(); f.Probe.Action = (_, _, _) => { f.Runtime.Running = true; throw new IOException(); };
    Check("an externally started Codex prevents rollback while it is active", await Reject(() => f.Switch()) && f.Vault.HasPending && DesktopLogin.Inspect(f.Current()).AccountId == "account-b");
    Check("recovery also refuses to overwrite active login", await Reject(() => f.Switcher.RestoreAsync(default)) && f.Vault.HasPending);
    f.Runtime.Running = false;
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe);
    await restarted.RestoreAsync(default);
    Check("a restarted tool can recover the encrypted transaction", f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
}
{
    var f = New(); var foreign = Fixture.Login("c");
    f.Probe.Action = (_, _, _) => { f.Write(foreign); throw new IOException(); };
    Check("unexpected new login is never overwritten by rollback", await Reject(() => f.Switch()) && f.Current().SequenceEqual(foreign) && f.Vault.HasPending);
    Check("recovery preserves a later unrelated login", await Reject(() => f.Switcher.RestoreAsync(default)) && f.Current().SequenceEqual(foreign));
    Check("account cache deletion cannot erase a recovery transaction", await Reject(() => { f.Switcher.RemoveSaved(f.B); return Task.CompletedTask; }) && f.Vault.HasPending);
    f.Runtime.Running = true;
    Check("keeping a new login also waits for Codex to exit", await Reject(() => f.Switcher.KeepCurrentAsync([f.A, f.B], default)) && f.Vault.HasPending);
    f.Runtime.Running = false;
    await f.Switcher.KeepCurrentAsync([f.A, f.B], default);
    Check("explicit keep-current recovery unlocks future switches without altering the login", f.Current().SequenceEqual(foreign) && !f.Vault.HasPending && f.Probe.Calls == 1 && f.Runtime.Launches == 0);
}
{
    var f = New(); var identity = DesktopLogin.Inspect(Fixture.Login("b"));
    f.Vault.SavePending(new(1, f.Runtime.Home, f.B.Id, f.Original, identity, DesktopLogin.Fingerprint(Fixture.Login("b")), "prepared"));
    await f.Switcher.RestoreAsync(default);
    Check("crash before installation clears only the journal", f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
    f.Vault.SavePending(new(1, Path.Combine(f.Root, "unrelated"), f.B.Id, f.Original, identity, DesktopLogin.Fingerprint(Fixture.Login("b")), "installed"));
    Check("recovery cannot be redirected to another home", await Reject(() => f.Switcher.RestoreAsync(default)) && f.Current().SequenceEqual(f.Original) && f.Vault.HasPending);
}
{
    var f = New(); var installed = Fixture.Login("b");
    f.Vault.SavePending(new(1, f.Runtime.Home, f.B.Id, f.Original, DesktopLogin.Inspect(installed), DesktopLogin.Fingerprint(installed), "prepared"));
    f.Write(installed);
    await f.Switcher.RestoreAsync(default);
    Check("crash between replacement and journal update can restore the original", f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
    f.Vault.SavePending(new(1, f.Runtime.Home, f.B.Id, f.Original, DesktopLogin.Inspect(installed), DesktopLogin.Fingerprint(installed), "prepared"));
    var laterLogin = Fixture.Login("b", "new-external-login"); f.Write(laterLogin);
    Check("a prepared transaction preserves a later same-account login", await Reject(() => f.Switcher.RestoreAsync(default)) && f.Current().SequenceEqual(laterLogin) && f.Vault.HasPending);
}
{
    var f = New(); f.Vault.Save(f.A.Id, Fixture.Login("a", "old"), authorizedForSwitching: true);
    f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("a", "renewed")); throw new IOException(); };
    Check("same-account failure never restores a revoked pre-refresh token", await Reject(() => f.Switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default)) && f.Current().SequenceEqual(Fixture.Login("a", "renewed")) && !f.Vault.HasPending);
}
{
    var f = New(); var file = new DesktopAuthFile(f.Runtime.Home);
    f.Write(Fixture.Login("a", "new"));
    Check("compare-before-replace rejects concurrent auth changes", await Reject(() => { file.Replace(Fixture.Login("b"), DesktopLogin.Fingerprint(f.Original)); return Task.CompletedTask; }) && f.Current().SequenceEqual(Fixture.Login("a", "new")));
    Directory.CreateDirectory(Path.Combine(f.Runtime.Home, "secrets")); File.WriteAllText(Path.Combine(f.Runtime.Home, "secrets", "codex_auth.age"), "synthetic");
    Check("mixed encrypted storage is refused without migration", await Reject(() => { file.Read(); return Task.CompletedTask; }));
}
foreach (var method in new[] { LoginMethod.Browser, LoginMethod.DeviceCode })
{
    var f = New(); f.Runtime.Running = true;
    FakeEnrollmentSession? session = null;
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => session = new(home, "b") { RotateOnDispose = true });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    LoginPrompt? shown = null;
    var result = await switcher.AuthorizeAsync(f.B, method, prompt => shown = prompt, default);
    Check("enrollment works while desktop is running: " + method, result.Success && shown?.Method == method && f.Runtime.Launches == 0 && f.Probe.Calls == 0);
    Check("enrollment never changes active desktop credentials: " + method, f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
    Check("enrollment captures the final token after helper disposal: " + method, f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "enrolled-renewed")));
    Check("enrollment never logs out any session: " + method, session!.Requests.All(x => x != "account/logout"));
    Check("enrollment removes its plaintext temporary profile: " + method, !Directory.EnumerateFileSystemEntries(Path.Combine(f.Root, "data", "desktop-enrollment")).Any() && session.Disposed);
    var cache = File.ReadAllBytes(Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin"));
    Check("new enrollment is encrypted in the existing per-account vault: " + method, !Encoding.UTF8.GetString(cache).Contains("SYNTHETIC_REFRESH"));
}
{
    var f = New(); var originalB = f.Vault.Load(f.B.Id);
    FakeEnrollmentSession? session = null;
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => session = new(home, "a"));
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
    Check("wrong browser account cannot replace a saved desktop login", !result.Success && result.Message.Contains("不一致") && f.Vault.Load(f.B.Id).SequenceEqual(originalB) && f.Current().SequenceEqual(f.Original));
    Check("wrong-account failure disposes the helper before clearing its profile", session!.Disposed && !Directory.Exists(session.Home));
}
{
    var f = New(); var originalB = f.Vault.Load(f.B.Id);
    FakeEnrollmentSession? session = null;
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => session = new(home, "b") { HoldCompletion = true });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    using var cancel = new CancellationTokenSource();
    var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => cancel.Cancel(), cancel.Token);
    Check("cancelled enrollment preserves all prior logins", result.Cancelled && !result.Success && f.Vault.Load(f.B.Id).SequenceEqual(originalB) && f.Current().SequenceEqual(f.Original));
    Check("cancelled enrollment cancels only its own attempt then cleans files", session!.Requests.Contains("account/login/cancel") && !session.Requests.Contains("account/logout") && session.Disposed && !Directory.Exists(session.Home));
}
foreach (var failure in new[] { "network", "server-account", "empty-quota", "final-identity" })
{
    var f = New(); var originalB = f.Vault.Load(f.B.Id);
    FakeEnrollmentSession? session = null;
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => session = new(home, "b") { Failure = failure });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
    Check("failed enrollment preserves existing logins: " + failure, !result.Success && f.Vault.Load(f.B.Id).SequenceEqual(originalB) && f.Current().SequenceEqual(f.Original));
    Check("failed enrollment sanitizes errors and clears files: " + failure, !result.Message.Contains("SYNTHETIC_SECRET") && session!.Disposed && !Directory.Exists(session.Home));
}
{
    var f = New(); var savedHomes = new List<string>(); var name = "a";
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => { savedHomes.Add(home); return new FakeEnrollmentSession(home, name); });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var c = new AccountRecord { Name = "Synthetic C", Email = "c@example.invalid" };
    var d = new AccountRecord { Name = "Synthetic D", Email = "d@example.invalid" };
    var beforeB = switcher.GetMessage(f.B); var beforeC = switcher.GetMessage(c); var beforeD = switcher.GetMessage(d);
    var a = await switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default);
    Check("authorizing A does not change the status shown for B, C or D", a.Success && switcher.GetMessage(f.A).Contains(f.A.Name)
        && switcher.GetMessage(f.B) == beforeB && switcher.GetMessage(c) == beforeC && switcher.GetMessage(d) == beforeD);
    var afterA = switcher.GetMessage(f.A);
    name = "b";
    var b = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
    Check("selecting A after authorizing B still shows A's result", b.Success && switcher.GetMessage(f.A) == afterA
        && switcher.GetMessage(f.B).Contains(f.B.Name) && !switcher.GetMessage(f.A).Contains(f.B.Name));
    Check("accounts without authorization never inherit another account's success", !switcher.HasSaved(c) && !switcher.HasSaved(d)
        && switcher.GetMessage(c) == beforeC && switcher.GetMessage(d) == beforeD);
    Check("enrolling a second account preserves the first independent login", a.Success && b.Success && f.Vault.Load(f.A.Id).SequenceEqual(Fixture.Login("a", "enrolled")) && f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "enrolled")));
    Check("each enrollment has a fresh home distinct from the desktop", savedHomes.Distinct().Count() == 2 && savedHomes.All(home => home != f.Runtime.Home && !Directory.Exists(home)) && f.Current().SequenceEqual(f.Original));
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    Check("restart derives each account's authorization status from its own cache", restarted.GetMessage(f.A).Contains("已")
        && restarted.GetMessage(f.B).Contains("已") && restarted.GetMessage(c) == beforeC && restarted.GetMessage(d) == beforeD);
    f.B.Name = f.A.Name;
    Check("duplicate account names do not merge status messages", switcher.GetMessage(f.A) == afterA && switcher.GetMessage(f.B) == b.Message);
    var failed = await switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default);
    Check("a failed reauthorization updates only its own account's status", !failed.Success && switcher.GetMessage(f.A) == failed.Message
        && switcher.GetMessage(f.B) == b.Message && switcher.GetMessage(c) == beforeC);
    switcher.RemoveSaved(f.B);
    Check("removing a saved login clears its stale success message", !switcher.HasSaved(f.B) && switcher.GetMessage(f.B) == beforeC);
}
{
    var f = New(); var started = new TaskCompletionSource();
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "b") { HoldCompletion = true });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var beforeA = switcher.GetMessage(f.A);
    using var cancel = new CancellationTokenSource();
    var attempt = switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => started.SetResult(), cancel.Token);
    await started.Task;
    Check("authorization progress belongs only to the account being authorized", switcher.GetMessage(f.B).Contains(f.B.Name)
        && switcher.GetMessage(f.A) == beforeA);
    Check("enrollment cannot overlap a switch or save", await Reject(() => switcher.SwitchAsync(f.B, [f.A, f.B], "codex", default)) && await Reject(() => switcher.SaveCurrentAsync(f.A, default)));
    Check("a second enrollment is rejected while one is waiting", await Reject(() => switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default)));
    cancel.Cancel(); await attempt;
    Check("cancelling one authorization does not replace another account's status", switcher.GetMessage(f.B).Contains("取消")
        && switcher.GetMessage(f.A) == beforeA);
}
{
    var f = New(); var data = Path.Combine(f.Root, "data"); var temporaryRoot = Path.Combine(data, "desktop-enrollment");
    var stale = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stale);
    File.WriteAllBytes(Path.Combine(stale, "auth.json"), Fixture.Login("b"));
    var unrelated = Path.Combine(temporaryRoot, "unrecognized"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
    _ = new DesktopLoginEnrollment(data, _ => throw new Exception("No session should start"));
    Check("restart cleans an abandoned enrollment without touching unknown folders", !Directory.Exists(stale) && File.ReadAllText(Path.Combine(unrelated, "keep.txt")) == "keep");
}
foreach (var message in new[] { "refresh_token_invalidated", "refresh_token_reused", "Your authentication token has been revoked" })
{
    var f = New(); var rpc = new CodexRpcException(-32000, message + " SYNTHETIC_SECRET_BACKEND_PAYLOAD");
    f.Probe.Action = (_, _, _) => throw rpc;
    Check("invalidated sessions are distinguished from network failure: " + message, await Reject(() => f.Switch()) && rpc.LoginInvalidated && rpc.RequiresLogin && f.Switcher.GetMessage(f.B).Contains("官方作废") && f.Current().SequenceEqual(f.Original) && !f.Switcher.GetMessage(f.B).Contains("SYNTHETIC_SECRET"));
}
{
    var f = New(); var originalB = f.Vault.Load(f.B.Id); FileStream? locked = null;
    var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "b")
    { OnDispose = () => locked = new FileStream(Path.Combine(home, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read) });
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    try
    {
        var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
        Check("cleanup failure does not commit credentials or report success", !result.Success && f.Vault.Load(f.B.Id).SequenceEqual(originalB) && f.Current().SequenceEqual(f.Original));
    }
    finally { locked?.Dispose(); }
    _ = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), _ => throw new Exception("No session should start"));
    Check("a failed cleanup is retried on restart", !Directory.EnumerateDirectories(Path.Combine(f.Root, "data", "desktop-enrollment")).Any());
}
{
    var f = New(); var rpc = new CodexRpcException(-32000, "error sending request: SYNTHETIC_SECRET_BACKEND_PAYLOAD");
    f.Probe.Action = (_, _, _) => throw rpc;
    Check("network failure does not claim the login was invalidated", await Reject(() => f.Switch()) && !rpc.LoginInvalidated && !rpc.RequiresLogin && f.Switcher.GetMessage(f.B).Contains("不代表账号登录已失效") && f.Current().SequenceEqual(f.Original));
}
{
    var f = New(); var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "a"));
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    var result = await switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default);
    var saved = f.Vault.LoadSnapshot(f.A.Id);
    Check("fresh authorization persists an independent-session marker", result.Success && saved.PendingEnrollment);
    CryptographicOperations.ZeroMemory(saved.Login);
    f.Probe.Action = (_, _, _) =>
    {
        if (!f.Current().SequenceEqual(Fixture.Login("a", "enrolled"))) throw new Exception("Old desktop login overwrote fresh authorization");
        f.Write(Fixture.Login("a", "refreshed-enrollment")); return Task.FromResult(Fixture.Quota("a"));
    };
    // A restarted service must still use the new enrollment, not the old desktop snapshot.
    var restarted = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe);
    await restarted.SwitchAsync(f.A, [f.A, f.B], "codex", default);
    Check("same-account switch uses freshly authorized credentials after restart", f.Runtime.Launches == 1 && f.Current().SequenceEqual(Fixture.Login("a", "refreshed-enrollment")));
    saved = f.Vault.LoadSnapshot(f.A.Id);
    Check("successful launch hands token tracking back to desktop", !saved.PendingEnrollment && saved.Login.SequenceEqual(f.Current()));
    CryptographicOperations.ZeroMemory(saved.Login);
}
{
    var f = New(); var data = Path.Combine(f.Root, "data");
    var enrollment = new DesktopLoginEnrollment(data, home => new FakeEnrollmentSession(home, "a"));
    var switcher = new DesktopSwitcher(data, f.Runtime, f.Probe, enrollment);
    await switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default);
    await switcher.SwitchAsync(f.B, [f.A, f.B], "codex", default);
    var saved = f.Vault.LoadSnapshot(f.A.Id);
    Check("leaving an old desktop session cannot overwrite its pending new authorization", saved.PendingEnrollment && saved.Login.SequenceEqual(Fixture.Login("a", "enrolled")));
    CryptographicOperations.ZeroMemory(saved.Login);
    f.Runtime.Running = false;
    f.Probe.Action = (_, _, _) => Task.FromResult(Fixture.Quota("a"));
    await switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default);
    Check("returning to an independently authorized account uses its new login", f.Current().SequenceEqual(Fixture.Login("a", "enrolled")));
}
{
    var f = New(); var enrollment = new DesktopLoginEnrollment(Path.Combine(f.Root, "data"), home => new FakeEnrollmentSession(home, "a"));
    var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, enrollment);
    await switcher.AuthorizeAsync(f.A, LoginMethod.Browser, _ => { }, default);
    f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("a", "new-session-rotated")); throw new CodexRpcException(-32000, "network failure"); };
    Check("independent same-account failure restores the original session", await Reject(() => switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default)) && f.Current().SequenceEqual(f.Original) && !f.Vault.HasPending);
    var saved = f.Vault.LoadSnapshot(f.A.Id);
    Check("rollback retains the new independent token for a later retry", saved.PendingEnrollment && saved.Login.SequenceEqual(Fixture.Login("a", "new-session-rotated")));
    CryptographicOperations.ZeroMemory(saved.Login);
}
{
    var f = New(); var legacy = Fixture.Login("b", "legacy");
    var encrypted = ProtectedData.Protect(legacy, Encoding.UTF8.GetBytes("QuotaOrb/desktop-login/v1/account/" + f.B.Id), DataProtectionScope.CurrentUser);
    File.WriteAllBytes(Path.Combine(f.Root, "data", "desktop-logins", f.B.Id + ".bin"), encrypted);
    var saved = f.Vault.LoadSnapshot(f.B.Id);
    Check("previous preview login cache remains readable", !saved.PendingEnrollment && saved.Login.SequenceEqual(legacy));
    CryptographicOperations.ZeroMemory(saved.Login);
}
await AuthorizationChecks.RunAsync(root, Check);
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { success = true, checks }));
Console.WriteLine($"{checks.Count} desktop switch checks passed. Synthetic test output: {root}");

sealed class Fixture
{
    public const string Config = "cli_auth_credentials_store = \"file\"\n# synthetic configuration to preserve\n";
    public string Root { get; }
    public FakeRuntime Runtime { get; }
    public FakeProbe Probe { get; } = new();
    public DesktopLoginVault Vault { get; }
    public DesktopSwitcher Switcher { get; }
    public AccountRecord A { get; } = new() { Name = "Synthetic A", Email = "a@example.invalid" };
    public AccountRecord B { get; } = new() { Name = "Synthetic B", Email = "b@example.invalid" };
    public byte[] Original { get; } = Login("a", "latest");
    public Fixture(string root)
    {
        Root = root; Runtime = new(Path.Combine(root, "home")); Directory.CreateDirectory(Runtime.Home);
        File.WriteAllText(Path.Combine(Runtime.Home, "config.toml"), Config);
        File.WriteAllText(Path.Combine(Runtime.Home, "chat-marker.txt"), "untouched"); Write(Original);
        Vault = new(Path.Combine(root, "data"));
        Vault.Save(A.Id, Login("a", "old"), authorizedForSwitching: true);
        Vault.Save(B.Id, Login("b"), authorizedForSwitching: true);
        Switcher = new(Path.Combine(root, "data"), Runtime, Probe);
    }
    public Task Switch() => Switcher.SwitchAsync(B, [A, B], "codex", default);
    public byte[] Current() => File.ReadAllBytes(Path.Combine(Runtime.Home, "auth.json"));
    public void Write(byte[] bytes) => File.WriteAllBytes(Path.Combine(Runtime.Home, "auth.json"), bytes);
    public static byte[] Login(string name, string revision = "initial")
    {
        var claims = new Dictionary<string, object> { ["email"] = name + "@example.invalid", ["jti"] = revision, ["https://api.openai.com/auth"] = new { chatgpt_account_id = "account-" + name, chatgpt_user_id = "user-" + name } };
        var token = "synthetic." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".unsigned";
        return JsonSerializer.SerializeToUtf8Bytes(new { auth_mode = "chatgpt", tokens = new { access_token = token, id_token = token, account_id = "account-" + name, refresh_token = "SYNTHETIC_REFRESH_" + name + "_" + revision } });
    }
    public static QuotaSnapshot Quota(string name, double used = 25) => new(DateTimeOffset.UtcNow, "account-" + name, used < 100, new() { ["codex"] = new("codex", null, "plus", new(used, 300, DateTimeOffset.UtcNow.AddHours(5)), new(25, 10080, DateTimeOffset.UtcNow.AddDays(7)), null, false, null) }, 1, []);
}
sealed class FakeRuntime(string home) : IDesktopRuntime
{
    public string Home => home;
    public bool Running { get; set; }
    public bool FailLaunch { get; set; }
    public int Launches { get; private set; }
    public Task ValidateAsync(CancellationToken token) => Task.CompletedTask;
    public void RequireStopped() { if (Running) throw new InvalidOperationException("Synthetic desktop still running"); }
    public Task LaunchAsync(CancellationToken token) { Launches++; if (FailLaunch) throw new IOException(); Running = true; return Task.CompletedTask; }
}
sealed class FakeProbe : IDesktopLoginProbe
{
    public int Calls { get; private set; }
    public Func<string, DesktopIdentity, CancellationToken, Task<QuotaSnapshot>> Action { get; set; } = (_, _, _) => Task.FromResult(Fixture.Quota("b"));
    public Task<QuotaSnapshot> VerifyAsync(string home, DesktopIdentity expected, CancellationToken token) { Calls++; return Action(home, expected, token); }
}

sealed class FakeEnrollmentSession(string home, string name) : IDesktopEnrollmentSession
{
    public string Home => home;
    public List<string> Requests { get; } = [];
    public bool Disposed { get; private set; }
    public bool RotateOnDispose { get; init; }
    public bool HoldCompletion { get; init; }
    public string? Failure { get; init; }
    public Action? OnDispose { get; init; }
    public Task Completion { get; } = new TaskCompletionSource().Task;
    public event Action<string, JsonElement>? Notification;
    public Task InitializeAsync(CancellationToken token)
    {
        if (File.Exists(Path.Combine(home, "auth.json"))) throw new Exception("Enrollment reused a login file");
        return Task.CompletedTask;
    }
    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(method);
        switch (method)
        {
            case "account/login/start":
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture.Login(name, "enrolled"));
                if (!HoldCompletion) Notification?.Invoke("account/login/completed", JsonSerializer.SerializeToElement(new { loginId = "synthetic-id", success = true }));
                return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "synthetic-id", authUrl = "https://auth.openai.com/authorize", verificationUrl = "https://auth.openai.com/codex/device", userCode = "TEST-0000" }));
            case "account/login/cancel": return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            case "account/read": return Task.FromResult(JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = name + "@example.invalid" } }));
            case "account/rateLimits/read":
                if (Failure == "network") throw new CodexRpcException(-32000, "error sending request: SYNTHETIC_SECRET_BACKEND_PAYLOAD");
                if (Failure == "empty-quota") return Task.FromResult(JsonSerializer.SerializeToElement(new { accountId = "account-" + name }));
                return Task.FromResult(JsonSerializer.SerializeToElement(new { accountId = Failure == "server-account" ? "account-other" : "account-" + name, rateLimits = new { limitId = "codex", primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds() } } }));
            default: throw new Exception("Unexpected method: " + method);
        }
    }
    public ValueTask DisposeAsync()
    {
        if (RotateOnDispose) File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture.Login(name, "enrolled-renewed"));
        if (Failure == "final-identity") File.WriteAllBytes(Path.Combine(home, "auth.json"), Fixture.Login("c", "enrolled"));
        Disposed = true; OnDispose?.Invoke(); return ValueTask.CompletedTask;
    }
}
