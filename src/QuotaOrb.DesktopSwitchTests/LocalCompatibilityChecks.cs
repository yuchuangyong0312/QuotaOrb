using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using QuotaOrb.Services;

// Opt-in local compatibility checks; never launch, switch, or refresh the real desktop login.
internal static class LocalCompatibilityChecks
{
    public static async Task RunAsync()
    {
        var runtime = new WindowsDesktopRuntime(new CodexInstallation());
        await runtime.ValidateAsync(CancellationToken.None);
        var bytes = new DesktopAuthFile(runtime.Home).Read();
        try { DesktopLogin.Inspect(bytes); Console.WriteLine("PASS: installed desktop version and local login format are supported (read only)"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var blocked = false;
        try { runtime.RequireStopped(); } catch (InvalidOperationException) { blocked = true; }
        Console.WriteLine("Running desktop blocks mutation: " + blocked);
        await CheckIsolatedProtocolAsync();
    }

    public static async Task CheckIsolatedProtocolAsync()
    {
        var executable = new CodexInstallation().RequireExecutable();
        var parent = Path.Combine(Path.GetTempPath(), "quota-orb-compatibility");
        var home = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        SwitchFiles.PrivateDirectory(home);
        try
        {
            var config = Fixture.Config;
            File.WriteAllText(Path.Combine(home, "config.toml"), config);
            var original = Fixture.Login("a");
            File.WriteAllBytes(Path.Combine(home, "auth.json"), original);
            var authFile = new DesktopAuthFile(home);
            foreach (var name in new[] { "a", "b", "a" })
            {
                var before = authFile.Read();
                var claims = new Dictionary<string, object> { ["email"] = name + "@example.invalid", ["sub"] = "user-" + name, ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["https://api.openai.com/profile"] = new { email = name + "@example.invalid" }, ["https://api.openai.com/auth"] = new { chatgpt_account_id = "account-" + name, chatgpt_user_id = "user-" + name, chatgpt_plan_type = "plus" } };
                var jwt = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
                var replacement = JsonSerializer.SerializeToUtf8Bytes(new { auth_mode = "chatgpt", last_refresh = DateTimeOffset.UtcNow, tokens = new { access_token = jwt, id_token = jwt, account_id = "account-" + name, refresh_token = "SYNTHETIC_REFRESH_" + name } });
                authFile.Replace(replacement, DesktopLogin.Fingerprint(before));
                var info = new ProcessStartInfo(executable) { WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-c", "cli_auth_credentials_store=\"file\"", "login", "status" }) info.ArgumentList.Add(argument);
                info.Environment["CODEX_HOME"] = home;
                foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_IDENTITY_PROVIDER_ID" }) info.Environment.Remove(key);
                using (var process = Process.Start(info)!)
                using (var job = new ChildProcessJob(process))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    var text = await output + await errors;
                    if (process.ExitCode != 0 || !text.Contains("Logged in using ChatGPT"))
                        throw new InvalidDataException("Installed helper did not accept the isolated ChatGPT file login.");
                }
                if (!authFile.Read().SequenceEqual(replacement) || File.ReadAllText(Path.Combine(home, "config.toml")) != config)
                    throw new InvalidDataException("Identity read unexpectedly changed the synthetic login or configuration.");
            }
            Console.WriteLine("PASS: installed helper accepts synthetic ChatGPT file logins A/B/A across restarts without changing credentials or configuration");
        }
        finally { SwitchFiles.DeletePrivateAttempt(parent, home); }
        if (Directory.Exists(home)) throw new IOException("Compatibility profile cleanup failed.");
        Console.WriteLine("PASS: isolated compatibility profile removed");
    }

    public static async Task CheckLiveAccessReadOnlyAsync()
    {
        var installation = new CodexInstallation();
        var auth = new DesktopAuthFile(new WindowsDesktopRuntime(installation).Home);
        var bytes = auth.Read();
        var parent = Path.Combine(Path.GetTempPath(), "quota-orb-compatibility");
        var home = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        SwitchFiles.PrivateDirectory(home);
        try
        {
            var identity = DesktopLogin.Inspect(bytes);
            await using (var client = CodexClient.ForAuthorizationCheck(installation.RequireExecutable(), home))
            {
                await client.InitializeAsync(default);
                await client.RequestAsync("account/login/start", new { type = "chatgptAuthTokens", accessToken = DesktopLogin.AccessToken(bytes), chatgptAccountId = identity.AccountId }, default);
                var response = await client.RequestAsync("account/read", new { refreshToken = false }, default);
                var account = QuotaParser.Get(response, "account");
                if (account is not { } value || QuotaParser.Text(value, "email") != identity.Email)
                    throw new InvalidDataException("Official identity read did not match the provided access token.");
                var limits = await client.RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, default);
                var quota = QuotaParser.Parse(limits, DateTimeOffset.UtcNow);
                if (quota.ServerAccountId != identity.AccountId || quota.Buckets.Count == 0)
                    throw new InvalidDataException("Official quota response did not match the expected account.");
            }
            var after = auth.Read();
            try
            {
                if (!bytes.SequenceEqual(after) || File.Exists(Path.Combine(home, "auth.json")))
                    throw new InvalidDataException("Login state changed during the read-only check.");
            }
            finally { CryptographicOperations.ZeroMemory(after); }
            Console.WriteLine("PASS: official identity and quota interfaces work with access-token-only mode; live login unchanged and no copied credential file");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); SwitchFiles.DeletePrivateAttempt(parent, home); }
        if (Directory.Exists(home)) throw new IOException("Compatibility profile cleanup failed.");
    }
}
