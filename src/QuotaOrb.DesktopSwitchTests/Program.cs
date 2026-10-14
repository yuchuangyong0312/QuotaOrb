global using QuotaOrb.Core;
global using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaOrb.Services;

if (args.Contains("--inspect-local-read-only"))
{
    var runtime = new WindowsDesktopRuntime(new CodexInstallation());
    await runtime.ValidateAsync(CancellationToken.None);
    var bytes = new DesktopAuthFile(runtime.Home).Read();
    try { DesktopLogin.Inspect(bytes); Console.WriteLine("PASS: installed desktop version and local login format are supported (read only)"); }
    finally { CryptographicOperations.ZeroMemory(bytes); }
    var blocked = false;
    try { runtime.RequireStopped(); } catch (InvalidOperationException) { blocked = true; }
    Console.WriteLine("Running desktop blocks mutation: " + blocked);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "quota-orb-switch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
async Task<bool> Reject(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or CryptographicException) { return true; } }
Fixture New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));

{
    var f = New(); await f.Switcher.SaveCurrentAsync(f.A, default);
    Check("saving current desktop login never invokes the network probe", f.Probe.Calls == 0 && f.Switcher.HasSaved(f.A));
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
    Check("raw backend errors never enter user-visible status", !f.Switcher.Message.Contains("SYNTHETIC_SECRET"));
}
{
    var f = New(); f.Probe.Action = (_, _, _) => { f.Write(Fixture.Login("b", "renewed")); return Task.FromResult(Fixture.Quota("b", 100)); };
    Check("exhausted target is not launched and original login is restored", await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && f.Runtime.Launches == 0);
    Check("rollback retains a target token renewed during verification", f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "renewed")));
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
foreach (var restriction in new[] { "spend-control", "individual", "reached-type" })
{
    var f = New(); var quota = Fixture.Quota("b"); var bucket = quota.Buckets["codex"];
    quota.Buckets["codex"] = restriction switch
    {
        "spend-control" => bucket with { SpendControlReached = true },
        "individual" => bucket with { IndividualRemaining = 0 },
        _ => bucket with { ReachedType = "synthetic-limit" }
    };
    f.Probe.Action = (_, _, _) => Task.FromResult(quota);
    Check("server restriction blocks positive percentages: " + restriction, await Reject(() => f.Switch()) && f.Current().SequenceEqual(f.Original) && f.Runtime.Launches == 0);
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
    var f = New(); f.Vault.Save(f.A.Id, Fixture.Login("a", "old"));
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
        Vault = new(Path.Combine(root, "data")); Vault.Save(B.Id, Login("b"));
        Switcher = new(Path.Combine(root, "data"), Runtime, Probe);
    }
    public Task Switch() => Switcher.SwitchAsync(B, [A, B], "codex", default);
    public byte[] Current() => File.ReadAllBytes(Path.Combine(Runtime.Home, "auth.json"));
    public void Write(byte[] bytes) => File.WriteAllBytes(Path.Combine(Runtime.Home, "auth.json"), bytes);
    public static byte[] Login(string name, string revision = "initial")
    {
        var claims = new Dictionary<string, object> { ["email"] = name + "@example.invalid", ["https://api.openai.com/auth"] = new { chatgpt_account_id = "account-" + name, chatgpt_user_id = "user-" + name } };
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
