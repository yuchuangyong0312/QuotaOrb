using System.Text.Json;
using System.Threading.Channels;

namespace QuotaOrb.Services;

public enum LoginMethod { Browser, DeviceCode }
public sealed record LoginPrompt(LoginMethod Method, Uri Url, string? UserCode, DateTimeOffset Deadline);
public sealed record LoginResult(bool Success, bool Cancelled, string Message);

internal interface ILoginConnection
{
    event Action<string, JsonElement>? Notification;
    Task Completion { get; }
    Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token);
}

// Owns just one authorization attempt. Never persists links, codes, or server error payloads.
internal static class LoginFlow
{
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(15);
    internal static Uri OfficialUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !new[] { "auth.openai.com", "auth0.openai.com", "chatgpt.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("官方登录地址无法验证，已停止打开。请更新 Codex 后重试。");
        return uri;
    }

    internal static async Task AuthorizeAsync(ILoginConnection client, LoginMethod method, Action<LoginPrompt> present,
        CancellationToken token, TimeSpan? waitLimit = null)
    {
        // Subscribe before start: completion can arrive before its RPC response.
        var events = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
        void Receive(string name, JsonElement data)
        {
            if (name == "account/login/completed") events.Writer.TryWrite(data.Clone());
        }
        client.Notification += Receive;
        string? loginId = null;
        var authorized = false;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var start = await client.RequestAsync("account/login/start", method == LoginMethod.Browser
                ? new { type = "chatgpt", useHostedLoginSuccessPage = true } : (object)new { type = "chatgptDeviceCode" }, token);
            loginId = QuotaParser.Text(start, "loginId");
            if (string.IsNullOrWhiteSpace(loginId)) throw new InvalidOperationException("未收到有效的授权会话，请更新 Codex 后重试。");
            var url = OfficialUrl(QuotaParser.Text(start, method == LoginMethod.Browser ? "authUrl" : "verificationUrl"));
            var code = method == LoginMethod.DeviceCode ? QuotaParser.Text(start, "userCode") : null;
            if (method == LoginMethod.DeviceCode && (string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(char.IsControl)))
                throw new InvalidOperationException("未收到有效设备码，请重新开始授权。");
            var limit = waitLimit ?? WaitLimit;
            attempt.CancelAfter(limit);
            present(new(method, url, code, DateTimeOffset.UtcNow.Add(limit)));
            while (true)
            {
                var next = events.Reader.ReadAsync(attempt.Token).AsTask();
                await Task.WhenAny(next, client.Completion);
                // A final completion notification takes precedence over transport EOF.
                if (!next.IsCompleted && client.Completion.IsCompleted)
                    throw new InvalidOperationException("授权组件已退出，本机回调已停止。请重新授权，或改用设备码授权。");
                var result = await next;
                if (QuotaParser.Text(result, "loginId") != loginId) continue;
                if (QuotaParser.Boolean(result, "success") != true)
                    throw new InvalidOperationException(method == LoginMethod.Browser
                        ? "本次官方授权未完成。请关闭旧登录网页后重试，或改用设备码授权。"
                        : "设备码授权未完成或已过期。请确认账号安全设置已允许设备码登录，再重新获取设备码。");
                authorized = true;
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt.IsCancellationRequested)
        {
            throw new InvalidOperationException("本次授权等待已超时，旧登录网页已失效。请重新授权，或改用设备码授权。");
        }
        finally
        {
            client.Notification -= Receive;
            attempt.Cancel();
            if (!authorized && loginId is not null && !client.Completion.IsCompleted)
            {
                try
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.RequestAsync("account/login/cancel", new { loginId }, cancel.Token);
                }
                catch { /* Disposal terminates only this tool-owned child if cancellation fails. */ }
            }
        }
    }
}
