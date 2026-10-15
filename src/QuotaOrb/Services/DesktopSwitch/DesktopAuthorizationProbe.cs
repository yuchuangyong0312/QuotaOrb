using System.Text.Json;

namespace QuotaOrb.Services;

public enum AuthorizationCheckState { Valid, Missing, Legacy, Unavailable, NeedsRefresh, Unconfirmed }
public sealed record AuthorizationCheckResult(string AccountId, string Name, AuthorizationCheckState State, DateTimeOffset CheckedAt)
{
    public string Summary => State switch
    {
        AuthorizationCheckState.Valid => "授权检查通过",
        AuthorizationCheckState.Missing => "尚未授权",
        AuthorizationCheckState.Legacy => "需要独立授权",
        AuthorizationCheckState.Unavailable => "本地授权无法读取或账号不匹配",
        AuthorizationCheckState.NeedsRefresh => "登录凭据需更新",
        _ => "暂时无法确认"
    };
    public string Advice => State switch
    {
        AuthorizationCheckState.Valid => "",
        AuthorizationCheckState.Missing or AuthorizationCheckState.Legacy or AuthorizationCheckState.Unavailable => "请点击“授权切换登录”完成授权。",
        AuthorizationCheckState.NeedsRefresh => "凭据可能过期；可尝试切换时自动刷新，仍失败再重新授权。",
        _ => "可能是网络、请求限制或服务异常，请稍后重试；不代表授权失效。"
    };
}

// This boundary deliberately receives only an access token, never a refresh token.
public interface IDesktopAuthorizationProbe
{
    Task<AuthorizationCheckState> CheckAsync(string accessToken, DesktopIdentity expected, CancellationToken token);
}

public sealed class DesktopAuthorizationProbe : IDesktopAuthorizationProbe
{
    private readonly string root;
    private readonly Func<string, IDesktopEnrollmentSession> connect;
    public DesktopAuthorizationProbe(string dataRoot, CodexInstallation installation)
        : this(dataRoot, home => CodexClient.ForAuthorizationCheck(installation.RequireExecutable(), home)) { }
    internal DesktopAuthorizationProbe(string dataRoot, Func<string, IDesktopEnrollmentSession> connect)
    {
        root = Path.Combine(Path.GetFullPath(dataRoot), "desktop-checks");
        this.connect = connect;
        SwitchFiles.PrivateDirectory(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) SwitchFiles.DeletePrivateAttempt(root, directory);
    }

    public async Task<AuthorizationCheckState> CheckAsync(string accessToken, DesktopIdentity expected, CancellationToken token)
    {
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"));
        SwitchFiles.PrivateDirectory(home);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = connect(home);
            await client.InitializeAsync(timeout.Token);
            await client.RequestAsync("account/login/start", new { type = "chatgptAuthTokens", accessToken, chatgptAccountId = expected.AccountId }, timeout.Token);
            // account/read alone merely echoes local claims. Require a backend response.
            var response = await client.RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, timeout.Token);
            var quota = QuotaParser.Parse(response, DateTimeOffset.UtcNow);
            return quota.ServerAccountId == expected.AccountId && quota.Buckets.Count > 0
                ? AuthorizationCheckState.Valid : AuthorizationCheckState.Unconfirmed;
        }
        catch (CodexRpcException error) { return error.RequiresLogin ? AuthorizationCheckState.NeedsRefresh : AuthorizationCheckState.Unconfirmed; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return AuthorizationCheckState.Unconfirmed; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return AuthorizationCheckState.Unconfirmed; }
        finally { SwitchFiles.DeletePrivateAttempt(root, home); }
    }
}
