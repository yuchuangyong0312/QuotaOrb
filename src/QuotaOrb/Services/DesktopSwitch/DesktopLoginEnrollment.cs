using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaOrb.Services;

internal interface IDesktopEnrollmentSession : ILoginConnection, IAsyncDisposable
{
    Task InitializeAsync(CancellationToken token);
}

// A separate official login session, never a copy of the quota profile or live desktop token.
// The official helper temporarily uses file storage in a private, disposable CODEX_HOME.
public sealed class DesktopLoginEnrollment
{
    private readonly string root;
    private readonly Func<string, IDesktopEnrollmentSession> connect;

    public DesktopLoginEnrollment(string dataRoot, CodexInstallation installation)
        : this(dataRoot, home => CodexClient.ForDesktopEnrollment(installation.RequireExecutable(), home)) { }

    internal DesktopLoginEnrollment(string dataRoot, Func<string, IDesktopEnrollmentSession> connect)
    {
        root = Path.Combine(Path.GetFullPath(dataRoot), "desktop-enrollment");
        this.connect = connect;
        SwitchFiles.PrivateDirectory(root);
        // Called once after acquiring the application's single-instance lock.
        // Only our GUID-named attempt directories can be removed after a crash.
        foreach (var directory in Directory.EnumerateDirectories(root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) DeleteAttempt(directory);
    }

    internal async Task<byte[]> AcquireAsync(AccountRecord account, LoginMethod method, Action<LoginPrompt> present, CancellationToken token)
    {
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"));
        byte[]? result = null;
        var cleaned = false;
        SwitchFiles.PrivateDirectory(home);
        try
        {
            DesktopIdentity expected;
            await using (var client = connect(home))
            {
                await client.InitializeAsync(token);
                await LoginFlow.AuthorizeAsync(client, method, present, token);
                var identity = await client.RequestAsync("account/read", new { refreshToken = false }, token);
                if (QuotaParser.Get(identity, "account") is not { ValueKind: JsonValueKind.Object } who
                    || QuotaParser.Text(who, "type") != "chatgpt"
                    || !string.Equals(QuotaParser.Text(who, "email"), account.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("网页中登录的账号与所选账号不一致，请选择正确账号重新授权。");
                var bytes = new DesktopAuthFile(home).Read();
                try { expected = DesktopLogin.Inspect(bytes); DesktopLogin.MatchAccount(expected, account); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                var quota = QuotaParser.Parse(await client.RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = false }, token), DateTimeOffset.UtcNow);
                if (quota.ServerAccountId != expected.AccountId || quota.Buckets.Count == 0)
                    throw new InvalidDataException("官方未返回匹配的账号额度，切换登录尚未保存，请重试。");
            }
            // Read after the helper exits so no background refresh can leave a stale snapshot.
            token.ThrowIfCancellationRequested();
            result = new DesktopAuthFile(home).Read();
            if (!DesktopLogin.Inspect(result).Matches(expected))
                throw new InvalidDataException("授权结束时账号发生变化，切换登录尚未保存。");
            DeleteAttempt(home);
            cleaned = true;
            var transferred = result; result = null;
            return transferred;
        }
        finally
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
            if (!cleaned) DeleteAttempt(home);
        }
    }

    private void DeleteAttempt(string directory)
    {
        try { SwitchFiles.DeletePrivateAttempt(root, directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("临时授权文件尚未清理，切换登录未保存。请重启额度工具后重新授权。"); }
    }
}
