using System.Diagnostics;
using System.Text.Json;

namespace QuotaOrb.Services;

public sealed class AccountService : IDisposable
{
    private readonly LocalStore store;
    private readonly CodexInstallation installation;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> retries = new();
    private int verificationEpoch;
    public AppData Data { get; }
    public string? BusyAccountId { get; private set; }
    public bool Busy => operation.CurrentCount == 0;
    public bool HasUnsavedChanges { get; private set; }
    public string? StorageError { get; private set; }
    public string Status { get; private set; } = "添加账号后，在官方页面完成登录授权。";
    public string? ComponentPath => installation.FindExecutable();
    public bool ComponentAvailable => ComponentPath is not null;
    public event Action? Changed;
    public event Action<string, string>? Notice;
    public AccountService(LocalStore store, CodexInstallation installation)
    {
        this.store = store; this.installation = installation; Data = store.Load();
        if (store.PendingPrivacyWork || store.PendingDataUpgrade)
        {
            HasUnsavedChanges = true;
            StorageError = "本地数据升级尚未完成。请检查目录权限，或关闭占用数据文件的程序，然后重试保存。";
        }
        Invalidate();
    }
    public bool Save()
    {
        if (shutdown.IsCancellationRequested) return false;
        var previousError = StorageError;
        var saved = false;
        try
        {
            store.Save(Data);
            saved = true;
            HasUnsavedChanges = store.PendingPrivacyWork;
            StorageError = store.PendingPrivacyWork ? "当前备注已加密，旧记录清理尚未完成。请关闭占用数据文件的程序后重试保存。" : null;
        }
        catch (Exception ex) when (LocalStore.IsStorageFailure(ex) || ex is JsonException)
        {
            HasUnsavedChanges = true;
            StorageError = "最新数据尚未保存。请检查磁盘空间、目录权限，或关闭占用数据文件的程序，然后重试保存。";
        }
        Changed?.Invoke();
        if (StorageError is not null && StorageError != previousError) Notice?.Invoke("数据尚未保存", StorageError);
        // Old-page cleanup can be retried without rolling back a successfully committed edit.
        return saved;
    }
    private bool BeginOperation() => !shutdown.IsCancellationRequested && operation.Wait(0);
    private void EndOperation() { BusyAccountId = null; operation.Release(); Changed?.Invoke(); }
    public async Task RunDesktopOperationAsync(AccountRecord? account, Func<CancellationToken, Task> action)
    {
        if ((account is not null && !Data.Accounts.Contains(account)) || !BeginOperation()) throw new InvalidOperationException("请等当前查询或授权结束。");
        try { BusyAccountId = account?.Id; Changed?.Invoke(); await action(shutdown.Token); }
        finally { EndOperation(); }
    }
    private void Commit(Action apply, Action undo)
    {
        if (!BeginOperation()) throw new InvalidOperationException("请等当前查询或授权结束，信息尚未保存。");
        try { CommitCore(apply, undo); }
        finally { EndOperation(); }
    }
    private void CommitCore(Action apply, Action undo)
    {
        var wasDirty = HasUnsavedChanges;
        apply();
        if (Save()) return;
        undo();
        HasUnsavedChanges = wasDirty || store.PendingPrivacyWork || store.PendingDataUpgrade;
        Changed?.Invoke();
        throw new IOException("保存失败，信息尚未写入。请检查磁盘空间与目录权限后重试。");
    }
    public void AddAccount(AccountRecord account)
    {
        if (Data.Accounts.Count >= 500) throw new InvalidOperationException("最多保存 500 个账号。");
        Commit(() => Data.Accounts.Add(account), () => Data.Accounts.Remove(account));
    }
    public void UpdateAccount(AccountRecord edited)
    {
        var index = Data.Accounts.FindIndex(a => a.Id == edited.Id);
        if (index < 0) throw new InvalidOperationException("此账号已不存在，请重新打开账号列表。");
        var original = Data.Accounts[index];
        var next = LocalStore.CopyAccount(original);
        next.Name = edited.Name; next.ProtectedPassword = edited.ProtectedPassword;
        if (original.Snapshot is null) next.Email = edited.Email;
        next.Notes = edited.Notes; next.ProtectedNotes = edited.ProtectedNotes; next.NotesUnavailable = edited.NotesUnavailable;
        next.RechargeDate = edited.RechargeDate; next.SubscriptionDate = edited.SubscriptionDate; next.Enabled = edited.Enabled;
        next.RechargeTime = edited.RechargeTime; next.SubscriptionTime = edited.SubscriptionTime;
        next.ProtectedTwoFactorUrl = edited.ProtectedTwoFactorUrl; next.ProtectedTwoFactorKey = edited.ProtectedTwoFactorKey;
        next.LoginInfoInitialized = edited.LoginInfoInitialized;
        Commit(() => Data.Accounts[index] = next, () => Data.Accounts[index] = original);
    }
    public int ImportAccounts(IEnumerable<AccountRecord> accounts)
    {
        var emails = Data.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Email)).Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = accounts.Where(a => string.IsNullOrWhiteSpace(a.Email) || emails.Add(a.Email)).ToList();
        if (Data.Accounts.Count + added.Count > 500) throw new InvalidOperationException("导入后账号数量超过 500。");
        Commit(() => Data.Accounts.AddRange(added), () => Data.Accounts.RemoveAll(a => added.Contains(a)));
        return added.Count;
    }
    public void UpdateSettings(AppSettings settings)
    {
        var original = Data.Settings;
        Commit(() => Data.Settings = settings, () => Data.Settings = original);
    }
    public void Invalidate()
    {
        verificationEpoch++;
        foreach (var a in Data.Accounts.Where(a => a.AuthState == "ready")) a.LastError = "等待重新核验";
        retries.Clear(); Changed?.Invoke();
    }

    public async Task RefreshAllAsync(bool force = false)
    {
        if (!BeginOperation()) return;
        try
        {
            Changed?.Invoke();
            foreach (var account in Data.Accounts.ToArray())
            {
                if (shutdown.IsCancellationRequested) break;
                if (!Data.Accounts.Contains(account) || !account.Enabled || account.AuthState != "ready") continue;
                if (!force && retries.TryGetValue(account.Id, out var retry) && retry.RetryAt > DateTimeOffset.UtcNow) continue;
                await RefreshCoreAsync(account);
                try { await Task.Delay(350, shutdown.Token); } catch (OperationCanceledException) { break; }
            }
        }
        finally { EndOperation(); }
    }

    public async Task RefreshAsync(AccountRecord account, bool force = true)
    {
        if (!Data.Accounts.Contains(account) || !BeginOperation()) return;
        try { await RefreshCoreAsync(account); }
        finally { EndOperation(); }
    }
    private async Task RefreshCoreAsync(AccountRecord account)
    {
        try
        {
            BusyAccountId = account.Id; Status = $"正在核验 {account.Name}…"; Changed?.Invoke();
            var epoch = verificationEpoch;
            var before = QuotaPolicy.Evaluate(account, Data.Settings.LimitId, DateTimeOffset.UtcNow);
            account.LastAttemptUtc = DateTimeOffset.UtcNow;
            await using var client = new CodexClient(installation.RequireExecutable(), store.ProfilePath(account.Id));
            await client.InitializeAsync(shutdown.Token);
            await ReadAccountAndQuotaAsync(client, account, epoch, shutdown.Token);
            retries.Remove(account.Id);
            Status = $"{account.Name} 已同步 · {DateTime.Now:HH:mm:ss}";
            var after = QuotaPolicy.Evaluate(account, Data.Settings.LimitId, DateTimeOffset.UtcNow);
            if (before.State is Availability.Exhausted or Availability.PendingReset && after.CanRecommend)
                Notice?.Invoke("账号额度已恢复", $"{account.Name} 的额度已重新核验，可以使用。");
        }
        catch (Exception ex) { HandleFailure(account, ex); }
        finally { BusyAccountId = null; Save(); }
    }

    public async Task<LoginResult> LoginAsync(AccountRecord account, CancellationToken cancellationToken,
        LoginMethod method = LoginMethod.Browser, Action<LoginPrompt>? present = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Data.Accounts.Contains(account) || !BeginOperation()) throw new InvalidOperationException("请等当前查询或授权结束。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        try
        {
            BusyAccountId = account.Id; Status = $"请在官方登录页面授权 {account.Name}，完成后此处会自动更新。"; Changed?.Invoke();
            await using var client = new CodexClient(installation.RequireExecutable(), store.ProfilePath(account.Id));
            await client.InitializeAsync(linked.Token);
            await LoginFlow.AuthorizeAsync(client, method, prompt =>
            {
                if (present is not null) present(prompt);
                else if (method == LoginMethod.Browser) Process.Start(new ProcessStartInfo(prompt.Url.AbsoluteUri) { UseShellExecute = true });
                else throw new InvalidOperationException("设备码授权需要在授权窗口中启动。");
            }, linked.Token);
            account.LastAttemptUtc = DateTimeOffset.UtcNow;
            await ReadAccountAndQuotaAsync(client, account, verificationEpoch, linked.Token);
            retries.Remove(account.Id);
            Status = $"{account.Name} 已授权并同步。";
            return new(true, false, Status);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            account.LastError = "已取消授权，旧登录网页已失效。请从工具重新开始授权。";
            Status = $"{account.Name}：{account.LastError}";
            return new(false, true, account.LastError);
        }
        catch (Exception ex)
        {
            HandleFailure(account, ex);
            if (ex is OperationCanceledException)
                account.LastError = "授权或额度查询请求超时。请检查网络后重试，或改用设备码授权。";
            else if (ex is IOException)
                account.LastError = "授权组件连接中断。请重新开始授权，或改用设备码授权。";
            else if (ex is CodexRpcException && method == LoginMethod.DeviceCode)
                account.LastError = "设备码授权或查询未成功。请检查网络，并确认 ChatGPT 安全设置已允许设备码登录。";
            Status = $"{account.Name}：{account.LastError}";
            return new(false, false, account.LastError!);
        }
        finally { try { Save(); } finally { EndOperation(); } }
    }

    private async Task ReadAccountAndQuotaAsync(CodexClient client, AccountRecord account, int epoch, CancellationToken token)
    {
        var identity = await client.RequestAsync("account/read", new { refreshToken = false }, token);
        if (QuotaParser.Get(identity, "account") is not { ValueKind: JsonValueKind.Object } who || QuotaParser.Text(who, "type") != "chatgpt")
        { account.AuthState = "login_required"; throw new InvalidOperationException("请使用 ChatGPT 账号完成官方登录授权。"); }
        var email = QuotaParser.Text(who, "email");
        if (!string.IsNullOrWhiteSpace(account.Email) && !string.Equals(account.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        { account.AuthState = "login_required"; throw new InvalidOperationException("登录账号与此条目中的邮箱不一致，请在浏览器中切换到对应账号后重新授权。"); }
        account.AuthState = "ready";
        if (email is not null) account.Email = email;
        account.Plan = QuotaParser.Text(who, "planType") ?? "unknown";
        var response = await client.RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = false }, token);
        var next = QuotaParser.Parse(response, DateTimeOffset.UtcNow);
        if (account.Snapshot?.ServerAccountId is { Length: > 0 } previousId && next.ServerAccountId is { Length: > 0 } newId && previousId != newId)
        { account.AuthState = "login_required"; throw new InvalidOperationException("授权的账号或工作区已改变。请新增独立条目，避免混用额度历史。"); }
        if (epoch != verificationEpoch) throw new InvalidOperationException("运行环境刚恢复，等待重新核验。");
        if (email is not null) account.Email = email;
        account.Plan = QuotaParser.Text(who, "planType") ?? "unknown";
        QuotaPolicy.AcceptSnapshot(account, next);
    }

    public async Task RemoveAsync(AccountRecord account)
    {
        if (!Data.Accounts.Contains(account)) return;
        if (!BeginOperation()) throw new InvalidOperationException("请等当前查询或授权结束。");
        try
        {
            BusyAccountId = account.Id; Changed?.Invoke();
            var localDraft = account.AuthState == "unlinked" && account.Snapshot is null && account.LastSuccessUtc is null
                && account.History.Count == 0 && !Path.Exists(store.ProfilePath(account.Id));
            if (!localDraft)
            {
                // Logout touches only the account's isolated credential-store entry.
                await using var client = new CodexClient(installation.RequireExecutable(), store.ProfilePath(account.Id));
                await client.InitializeAsync(shutdown.Token);
                await client.RequestAsync("account/logout", new { }, shutdown.Token);
                account.AuthState = "unlinked"; account.LastError = "此工具保存的授权已注销，请重新授权。";
                HasUnsavedChanges = true;
            }
            var index = Data.Accounts.IndexOf(account);
            CommitCore(() => Data.Accounts.RemoveAt(index), () => Data.Accounts.Insert(index, account));
            retries.Remove(account.Id);
        }
        finally { EndOperation(); }
    }

    private void HandleFailure(AccountRecord account, Exception error)
    {
        if (error is CodexRpcException { RequiresLogin: true }) account.AuthState = "login_required";
        var message = error switch
        {
            OperationCanceledException => "操作已取消或超时。",
            CodexRpcException rpc => rpc.Throttled ? "查询过于频繁，已延后重试。" : rpc.Message,
            FileNotFoundException => CodexInstallation.MissingMessage,
            InvalidOperationException => error.Message,
            _ => "查询失败，请检查网络连接后重试。"
        };
        account.LastError = message; Status = $"{account.Name}：{message}";
        var failures = retries.TryGetValue(account.Id, out var previous) ? previous.Failures + 1 : 1;
        // The RPC surface does not expose HTTP Retry-After; use conservative backoff without tight retries.
        var minutes = error is CodexRpcException { Throttled: true } ? 30 : Math.Min(60, 2 * Math.Pow(2, Math.Min(failures - 1, 5)));
        retries[account.Id] = (failures, DateTimeOffset.UtcNow.AddMinutes(minutes));
        if (account.AuthState == "login_required") Notice?.Invoke("账号需要登录", $"{account.Name} 的授权需要更新。");
    }

    public void Dispose() { shutdown.Cancel(); }
}
