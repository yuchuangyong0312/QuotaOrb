using System.Text.Json;
using QuotaOrb.Services;

internal static class AuthorizationChecks
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        Fixture New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
        {
            var f = New(); f.Runtime.Running = true; f.B.Enabled = false;
            var probe = new FakeAuthorizationProbe();
            var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, authorizationProbe: probe);
            try { await switcher.SwitchAsync(f.A, [f.A, f.B], "codex", default); } catch (InvalidOperationException) { }
            check("previous running-desktop warning does not replace the authorization state", switcher.GetOperationMessage(f.A)!.StartsWith("上次操作：") && switcher.GetAuthorizationMessage(f.A).Contains("已保存切换授权"));
            var path = Path.Combine(f.Root, "data", "desktop-logins");
            var original = Directory.GetFiles(path).ToDictionary(p => p, File.ReadAllBytes);
            List<AuthorizationCheckResult> results = [];
            await switcher.CheckAllAuthorizationsAsync([f.A, f.B], results.Add, default);
            check("all accounts including disabled accounts are checked while desktop is running", results.Count == 2 && results.All(r => r.State == AuthorizationCheckState.Valid));
            check("checks never launch, switch, rotate or rewrite credentials", f.Runtime.Launches == 0 && f.Probe.Calls == 0 && f.Current().SequenceEqual(f.Original) && original.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && !f.Vault.HasPending);
            check("active account uses latest desktop access token and other account uses own saved token", probe.Seen.SequenceEqual(new[] { DesktopLogin.AccessToken(f.Original), DesktopLogin.AccessToken(f.Vault.Load(f.B.Id)) }));
            check("new results clear old operation notices and remain account scoped", switcher.GetOperationMessage(f.A) is null && switcher.GetAuthorizationMessage(f.A).Contains(f.A.Name) && !switcher.GetAuthorizationMessage(f.A).Contains(f.B.Name));
            f.Vault.Save(f.A.Id, Fixture.Login("a", "new-enrollment"), pendingEnrollment: true);
            probe.Seen.Clear(); await switcher.CheckAllAuthorizationsAsync([f.A], _ => { }, default);
            check("fresh independent authorization takes precedence over older active desktop login", probe.Seen.Single() == DesktopLogin.AccessToken(Fixture.Login("a", "new-enrollment")));
        }
        {
            var f = New(); var probe = new FakeAuthorizationProbe();
            var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, authorizationProbe: probe);
            f.Vault.Remove(f.A.Id); f.Vault.Save(f.B.Id, Fixture.Login("b"));
            List<AuthorizationCheckResult> results = [];
            await switcher.CheckAllAuthorizationsAsync([f.A, f.B], results.Add, default);
            check("missing and legacy records are not presented as valid and never sent to backend", results.Select(r => r.State).SequenceEqual(new[] { AuthorizationCheckState.Missing, AuthorizationCheckState.Legacy }) && probe.Seen.Count == 0);
            f.Vault.Save(f.B.Id, Fixture.Login("a"), authorizedForSwitching: true);
            results.Clear(); await switcher.CheckAllAuthorizationsAsync([f.B], results.Add, default);
            check("wrong-identity cache is rejected before any backend request", results.Single().State == AuthorizationCheckState.Unavailable && probe.Seen.Count == 0);
        }
        {
            var f = New(); var probe = new FakeAuthorizationProbe { Action = (_, id, _) => id.AccountId == "account-a" ? throw new IOException("SYNTHETIC_SECRET") : Task.FromResult(AuthorizationCheckState.NeedsRefresh) };
            var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, authorizationProbe: probe);
            List<AuthorizationCheckResult> results = [];
            await switcher.CheckAllAuthorizationsAsync([f.A, f.B], results.Add, default);
            check("one network failure does not stop remaining accounts or expose raw errors", results.Count == 2 && results[0].State == AuthorizationCheckState.Unconfirmed && results[1].State == AuthorizationCheckState.NeedsRefresh && !switcher.GetAuthorizationMessage(f.A).Contains("SYNTHETIC_SECRET"));
            check("inconclusive checks do not delete authorization or disable attempting a switch", switcher.HasAuthorized(f.A) && switcher.HasAuthorized(f.B));
        }
        {
            var f = New(); var entered = new TaskCompletionSource();
            var probe = new FakeAuthorizationProbe { Action = async (_, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return AuthorizationCheckState.Valid; } };
            var switcher = new DesktopSwitcher(Path.Combine(f.Root, "data"), f.Runtime, f.Probe, authorizationProbe: probe);
            using var cancel = new CancellationTokenSource();
            var task = switcher.CheckAllAuthorizationsAsync([f.A, f.B], _ => throw new Exception("unexpected completed check"), cancel.Token);
            await entered.Task;
            var blocked = false;
            try { await switcher.SwitchAsync(f.B, [f.A, f.B], "codex", default); } catch (InvalidOperationException) { blocked = true; }
            check("a batch check excludes simultaneous switch mutations", blocked && f.Current().SequenceEqual(f.Original));
            cancel.Cancel(); var cancelled = false;
            try { await task; } catch (OperationCanceledException) { cancelled = true; }
            probe.Action = (_, _, _) => Task.FromResult(AuthorizationCheckState.Valid);
            await switcher.CheckAllAuthorizationsAsync([f.B], _ => { }, default);
            check("cancelling a batch releases the lock and leaves credentials intact", cancelled && f.Current().SequenceEqual(f.Original));
        }
        foreach (var scenario in new[] { "zero-quota", "wrong-account", "empty", "expired", "network" })
        {
            var data = Path.Combine(root, Guid.NewGuid().ToString("N"));
            FakeAuthorizationSession? session = null;
            var probe = new DesktopAuthorizationProbe(data, home => session = new(home, scenario));
            var state = await probe.CheckAsync(DesktopLogin.AccessToken(Fixture.Login("b")), DesktopLogin.Inspect(Fixture.Login("b")), default);
            var expected = scenario == "zero-quota" ? AuthorizationCheckState.Valid : scenario == "expired" ? AuthorizationCheckState.NeedsRefresh : AuthorizationCheckState.Unconfirmed;
            check("official check handles " + scenario, state == expected && session!.Disposed && session.Methods.SequenceEqual(new[] { "account/login/start", "account/rateLimits/read" }));
            check("isolated check sends no refresh token and cleans its profile: " + scenario, session!.AccessOnly && !Directory.EnumerateDirectories(Path.Combine(data, "desktop-checks")).Any());
        }
        {
            var home = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
            await using var client = CodexClient.ForAuthorizationCheck(Environment.ProcessPath!, home);
            await client.InitializeAsync(default);
            var rejected = false;
            try { await client.RequestAsync("account/rateLimits/read", new { }, default); }
            catch (CodexRpcException e) { rejected = e.RequiresLogin; }
            check("external refresh request with colliding RPC id is refused promptly", rejected && !File.Exists(Path.Combine(home, "auth.json")));
        }
    }

    public static async Task RunRpcHelperAsync()
    {
        int? waiting = null;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line); var value = doc.RootElement;
            if (!value.TryGetProperty("id", out var id)) continue;
            if (value.TryGetProperty("error", out _) && waiting is { } pending)
            { Console.WriteLine(JsonSerializer.Serialize(new { id = pending, error = new { code = -32000, message = "401 unauthorized; sign in" } })); waiting = null; continue; }
            var method = value.GetProperty("method").GetString();
            if (method == "account/rateLimits/read")
            { waiting = id.GetInt32(); Console.WriteLine(JsonSerializer.Serialize(new { id = waiting, method = "account/chatgptAuthTokens/refresh", @params = new { reason = "unauthorized" } })); }
            else Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { } }));
        }
    }
}

sealed class FakeAuthorizationProbe : IDesktopAuthorizationProbe
{
    public List<string> Seen { get; } = [];
    public Func<string, DesktopIdentity, CancellationToken, Task<AuthorizationCheckState>> Action { get; set; } = (_, _, _) => Task.FromResult(AuthorizationCheckState.Valid);
    public Task<AuthorizationCheckState> CheckAsync(string accessToken, DesktopIdentity expected, CancellationToken token)
    { Seen.Add(accessToken); return Action(accessToken, expected, token); }
}

sealed class FakeAuthorizationSession(string home, string scenario) : IDesktopEnrollmentSession
{
    public bool AccessOnly { get; private set; }
    public bool Disposed { get; private set; }
    public List<string> Methods { get; } = [];
    public Task Completion { get; } = new TaskCompletionSource().Task;
    public event Action<string, JsonElement>? Notification { add { } remove { } }
    public Task InitializeAsync(CancellationToken token)
    { if (File.Exists(Path.Combine(home, "auth.json"))) throw new Exception("reused auth file"); return Task.CompletedTask; }
    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        Methods.Add(method);
        if (method == "account/login/start")
        {
            var data = JsonSerializer.SerializeToElement(parameters);
            AccessOnly = data.GetProperty("type").GetString() == "chatgptAuthTokens" && data.EnumerateObject().Count() == 3 && !data.ToString().Contains("SYNTHETIC_REFRESH");
            return Task.FromResult(JsonSerializer.SerializeToElement(new { type = "chatgptAuthTokens" }));
        }
        if (method != "account/rateLimits/read") throw new Exception("unexpected request");
        if (scenario == "expired") throw new CodexRpcException(-32000, "401 expired SYNTHETIC_SECRET");
        if (scenario == "network") throw new CodexRpcException(-32000, "network SYNTHETIC_SECRET");
        if (scenario == "empty") return Task.FromResult(JsonSerializer.SerializeToElement(new { accountId = "account-b" }));
        return Task.FromResult(JsonSerializer.SerializeToElement(new { accountId = scenario == "wrong-account" ? "other" : "account-b", ordinaryUsageAllowed = false, rateLimits = new { limitId = "codex", primary = new { usedPercent = 100, windowDurationMins = 300 }, secondary = new { usedPercent = 100, windowDurationMins = 10080 } } }));
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
