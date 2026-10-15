using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuotaOrb.Services;

public sealed class CodexRpcException : Exception
{
    public int Code { get; }
    public bool RequiresLogin { get; }
    public bool Throttled { get; }
    public bool LoginInvalidated { get; }
    public CodexRpcException(int code, string message) : base(SafeMessage(message))
    {
        Code = code;
        LoginInvalidated = IsInvalidated(message);
        RequiresLogin = LoginInvalidated || message.Contains("401", StringComparison.OrdinalIgnoreCase) || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("not logged", StringComparison.OrdinalIgnoreCase) || message.Contains("sign in", StringComparison.OrdinalIgnoreCase) || message.Contains("refresh token", StringComparison.OrdinalIgnoreCase);
        Throttled = message.Contains("429", StringComparison.OrdinalIgnoreCase) || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsInvalidated(string value) => new[] { "refresh_token_invalidated", "refresh_token_reused", "refresh_token_expired", "token has been invalidated", "token has already been used", "session has been revoked", "token has been revoked" }
        .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    private static string SafeMessage(string value) => IsInvalidated(value) ? "保存的登录已失效，需要重新授权。"
        : value.Contains("keyring", StringComparison.OrdinalIgnoreCase) || value.Contains("credential store", StringComparison.OrdinalIgnoreCase)
        ? "Windows 凭据存储不可用，已停止授权；不会改为明文保存。"
        : "官方查询未成功。请检查网络或重新登录授权。";
}

public sealed class CodexClient : IAsyncDisposable, ILoginConnection, IDesktopEnrollmentSession
{
    private readonly Process process;
    private readonly ChildProcessJob job;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private readonly Task stderrDrain;
    private int sequence;
    private bool externalCheck;
    public Task Completion => reader;
    public event Action<string, JsonElement>? Notification;

    public CodexClient(string executable, string profile) : this(executable, profile, false) { }

    // Used only by the desktop switch transaction while every Codex process is stopped.
    internal static CodexClient ForDesktopVerification(string executable, string home) => new(executable, home, true);

    // The caller owns a fresh private profile. Never enroll in the live desktop home.
    internal static CodexClient ForDesktopEnrollment(string executable, string home) => new(executable, home, true);

    internal static CodexClient ForAuthorizationCheck(string executable, string home) => new(executable, home, true) { externalCheck = true };

    private CodexClient(string executable, string profile, bool desktopVerification)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException(CodexInstallation.MissingMessage, executable);
        if (desktopVerification) { if (!Directory.Exists(profile)) throw new DirectoryNotFoundException("Codex 登录目录不存在。"); }
        else Directory.CreateDirectory(profile);
        // Only ordinary quota profiles belong to QuotaOrb; desktop verification must preserve user config.
        if (!desktopVerification) File.WriteAllText(Path.Combine(profile, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n[analytics]\nenabled = false\n", new UTF8Encoding(false));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = profile, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        if (desktopVerification)
        {
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("analytics.enabled=false");
        }
        start.Environment["CODEX_HOME"] = profile;
        foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_IDENTITY_PROVIDER_ID" }) start.Environment.Remove(key);
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.Start();
        try { job = new ChildProcessJob(process); }
        catch { try { process.Kill(entireProcessTree:true); } catch { } process.Dispose(); throw; }
        reader = ReadAsync();
        // Authentication responses and diagnostics may contain sensitive details: never persist them.
        stderrDrain = DrainAsync(process.StandardError, lifetime.Token);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var clientInfo = new { name = "quota_orb", title = "额度管理", version = typeof(CodexClient).Assembly.GetName().Version?.ToString(3) ?? "2.0.0" };
        object parameters = externalCheck ? new { clientInfo, capabilities = new { experimentalApi = true } } : new { clientInfo };
        await RequestAsync("initialize", parameters, cancellationToken);
        await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken);
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (reader.IsCompleted) throw new IOException("查询组件已退出，请重试。");
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            await SendAsync(new { id, method, @params = parameters }, timeout.Token);
            await Task.WhenAny(completion.Task, reader);
            if (!completion.Task.IsCompleted) throw new IOException("查询组件已退出，请重试。");
            return await completion.Task;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object data, CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(), cancellationToken); await process.StandardInput.FlushAsync(cancellationToken); }
        finally { writes.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var message = doc.RootElement;
                    if (QuotaParser.Text(message, "method") is null && QuotaParser.Get(message, "id") is { ValueKind: JsonValueKind.Number } id && id.TryGetInt32(out var number) && pending.TryGetValue(number, out var waiter))
                    {
                        if (QuotaParser.Get(message, "error") is { ValueKind: JsonValueKind.Object } error)
                            waiter.TrySetException(new CodexRpcException(QuotaParser.Get(error, "code")?.GetInt32() ?? 0, QuotaParser.Text(error, "message") ?? ""));
                        else if (QuotaParser.Get(message, "result") is { } result) waiter.TrySetResult(result.Clone());
                    }
                    else if (externalCheck && QuotaParser.Get(message, "id") is { } requestId && QuotaParser.Text(message, "method") is not null)
                    {
                        // External-token checks must never rotate a saved desktop session.
                        // Reject refresh requests promptly instead of leaving the backend waiting.
                        await SendAsync(new { id = requestId.Clone(), error = new { code = -32000, message = "Token refresh is disabled for authorization checks; sign in again if needed." } }, lifetime.Token);
                    }
                    else if (QuotaParser.Text(message, "method") is { } method && QuotaParser.Get(message, "params") is { } parameters)
                        Notification?.Invoke(method, parameters.Clone());
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { foreach (var waiter in pending.Values) waiter.TrySetException(new IOException("查询组件已退出，请重试。")); }
    }

    private static async Task DrainAsync(StreamReader stream, CancellationToken token)
    { try { while (await stream.ReadLineAsync(token) is not null) { } } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { } }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { process.StandardInput.Close(); } catch { }
        try { if (!process.HasExited) { using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); } } } catch (InvalidOperationException) { }
        job.Dispose();
        try { await Task.WhenAll(reader, stderrDrain); } catch { }
        process.Dispose(); lifetime.Dispose(); writes.Dispose();
    }
}
