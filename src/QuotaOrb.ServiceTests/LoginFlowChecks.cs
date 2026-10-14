using System.Text.Json;
using QuotaOrb.Services;

internal static class LoginFlowChecks
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var client = new Connection(); LoginPrompt? shown = null;
        client.OnStart = () =>
        {
            client.Emit("other", false); // An unrelated completion must not finish this attempt.
            client.Emit("current", true); // Sent before the start response.
        };
        await LoginFlow.AuthorizeAsync(client, LoginMethod.Browser, value => shown = value, CancellationToken.None);
        check("early completion is correlated by loginId and browser handoff is HTTPS", shown?.Url.Host == "auth.openai.com" && client.Cancels == 0);
        check("hosted success page avoids dependency on a local success-page server", client.Parameters.GetProperty("useHostedLoginSuccessPage").GetBoolean());
        check("authorization detaches notification handlers", client.Subscribers == 0);

        client = new Connection { Device = true }; shown = null;
        client.OnStart = () => client.Emit("current", true);
        await LoginFlow.AuthorizeAsync(client, LoginMethod.DeviceCode, p => shown = p, CancellationToken.None);
        check("device-code handoff carries a code without a loopback callback", shown?.UserCode == "TEST-0000" && shown.Url.AbsolutePath == "/codex/device" && client.Parameters.GetProperty("type").GetString() == "chatgptDeviceCode");

        client = new Connection(); client.OnStart = () => client.Emit("current", false);
        var message = await Failure(client);
        check("server failure leaves a safe retry message and cancels its own session", message.Contains("重新") || message.Contains("重试"));
        check("server error payload is never echoed", !message.Contains("SYNTHETIC_PRIVATE_SERVER_ERROR") && client.Cancels == 1);

        client = new Connection();
        message = await Failure(client, wait: TimeSpan.FromMilliseconds(30));
        check("expired attempts explain that the old browser page is invalid", message.Contains("超时") && message.Contains("旧登录网页") && client.Cancels == 1 && client.Subscribers == 0);

        client = new Connection(); using var cancel = new CancellationTokenSource();
        var waiting = LoginFlow.AuthorizeAsync(client, LoginMethod.Browser, _ => cancel.Cancel(), cancel.Token);
        var cancelled = false; try { await waiting; } catch (OperationCanceledException) { cancelled = true; }
        check("explicit user cancellation cancels only that login session", cancelled && client.Cancels == 1 && client.Subscribers == 0);

        client = new Connection(); client.OnStart = () => client.Exited.TrySetResult();
        message = await Failure(client);
        check("component exit is detected immediately instead of waiting for timeout", message.Contains("组件已退出") && client.Cancels == 0);

        foreach (var url in new[] { "http://auth.openai.com/", "https://auth.openai.com.evil.invalid/", "https://user@auth.openai.com/", "file:///C:/test", "https://auth.openai.com:8443/" })
        {
            client = new Connection { Url = url };
            message = await Failure(client);
            check("unsafe authorization URL rejected: " + url, message.Contains("无法验证") && client.Cancels == 1);
        }
        client = new Connection();
        var uiFailed = false;
        try { await LoginFlow.AuthorizeAsync(client, LoginMethod.Browser, _ => throw new InvalidOperationException("Synthetic UI failure"), CancellationToken.None); }
        catch (InvalidOperationException) { uiFailed = true; }
        check("handoff presentation failure cleans up pending authorization", uiFailed && client.Cancels == 1 && client.Subscribers == 0);
        client = new Connection { Device = true, Code = "" };
        message = await Failure(client, method: LoginMethod.DeviceCode);
        check("missing device code is rejected instead of presenting unusable login", message.Contains("有效设备码") && client.Cancels == 1);
    }

    private static async Task<string> Failure(Connection client, TimeSpan? wait = null, LoginMethod method = LoginMethod.Browser)
    {
        try { await LoginFlow.AuthorizeAsync(client, method, _ => { }, CancellationToken.None, wait ?? TimeSpan.FromSeconds(2)); }
        catch (InvalidOperationException error) { return error.Message; }
        throw new Exception("Expected controlled login failure");
    }

    private sealed class Connection : ILoginConnection
    {
        private event Action<string, JsonElement>? received;
        public event Action<string, JsonElement>? Notification { add { received += value; Subscribers++; } remove { received -= value; Subscribers--; } }
        public int Subscribers, Cancels;
        public bool Device;
        public string Url = "https://auth.openai.com/authorize", Code = "TEST-0000";
        public Action? OnStart;
        public JsonElement Parameters;
        public TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => Exited.Task;
        public void Emit(string id, bool success) => received?.Invoke("account/login/completed", JsonSerializer.SerializeToElement(new { loginId = id, success, error = "SYNTHETIC_PRIVATE_SERVER_ERROR" }));
        public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == "account/login/cancel") { Cancels++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); }
            if (method != "account/login/start") throw new Exception("Unexpected method");
            Parameters = JsonSerializer.SerializeToElement(parameters);
            OnStart?.Invoke();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "current", authUrl = Url, verificationUrl = "https://auth.openai.com/codex/device", userCode = Code }));
        }
    }
}
