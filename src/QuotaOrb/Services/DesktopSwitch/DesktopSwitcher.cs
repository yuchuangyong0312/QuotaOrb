using System.Security.Cryptography;

namespace QuotaOrb.Services;

public interface IDesktopRuntime
{
    string Home { get; }
    Task ValidateAsync(CancellationToken token);
    void RequireStopped();
    Task LaunchAsync(CancellationToken token);
}

public interface IDesktopLoginProbe
{
    Task<QuotaSnapshot> VerifyAsync(string home, DesktopIdentity expected, CancellationToken token);
}

public sealed class DesktopSwitcher
{
    private readonly DesktopLoginVault vault;
    private readonly IDesktopRuntime runtime;
    private readonly IDesktopLoginProbe probe;
    private readonly DesktopAuthFile auth;
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool HasPending => vault.HasPending;
    public string Message { get; private set; } = "每个账号先在 Codex 登录一次，再保存桌面登录。切换前请从 Codex 托盘退出。";
    public event Action? Changed;

    public DesktopSwitcher(string dataRoot, IDesktopRuntime runtime, IDesktopLoginProbe probe)
    {
        vault = new(dataRoot); this.runtime = runtime; this.probe = probe; auth = new(runtime.Home);
    }

    public bool HasSaved(AccountRecord account) => vault.Has(account.Id);

    public async Task SaveCurrentAsync(AccountRecord account, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("已有切换操作正在进行。");
        byte[]? bytes = null;
        try
        {
            RequireNoPending();
            await runtime.ValidateAsync(token);
            bytes = auth.Read();
            DesktopLogin.MatchAccount(DesktopLogin.Inspect(bytes), account);
            // Saving does not refresh a token or create another active OAuth session.
            // A network verification is performed after installation, with Codex stopped.
            var again = auth.Read();
            try { if (!DesktopLogin.Same(bytes, again)) throw new IOException("登录状态正在更新，请稍后重新保存。"); }
            finally { CryptographicOperations.ZeroMemory(again); }
            token.ThrowIfCancellationRequested();
            vault.Save(account.Id, bytes);
            SetMessage($"已保存「{account.Name}」的桌面登录，切换时会重新核验。");
        }
        finally { Clear(bytes); gate.Release(); }
    }

    public async Task SwitchAsync(AccountRecord target, IReadOnlyList<AccountRecord> accounts, string pool, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("已有切换操作正在进行。");
        byte[]? original = null, replacement = null;
        SwitchJournal? journal = null;
        try
        {
            RequireNoPending();
            if (!target.Enabled) throw new InvalidOperationException("此账号已停用，请先启用后再切换。");
            runtime.RequireStopped();
            await runtime.ValidateAsync(token);
            runtime.RequireStopped();
            replacement = vault.Load(target.Id);
            var identity = DesktopLogin.Inspect(replacement);
            DesktopLogin.MatchAccount(identity, target);
            original = auth.Read();
            var outgoing = DesktopLogin.Inspect(original);
            var previous = accounts.Where(a => MatchesRecord(outgoing, a)).ToArray();
            // Save rotated tokens every time an account is left, never restore the original stale snapshot.
            foreach (var account in previous) vault.Save(account.Id, original);
            if (outgoing.Matches(identity))
            {
                Clear(replacement); replacement = original.ToArray();
                vault.Save(target.Id, original);
            }
            token.ThrowIfCancellationRequested();
            journal = new(1, runtime.Home, target.Id, original, identity, DesktopLogin.Fingerprint(replacement), "prepared");
            vault.SavePending(journal);
            runtime.RequireStopped();
            auth.Replace(replacement, DesktopLogin.Fingerprint(original));
            journal = journal with { Stage = "installed" }; vault.SavePending(journal);
            SetMessage("正在核验目标账号的登录与额度…");
            // The official helper may rotate a refresh token in the actual auth file.
            // It is disposed before launching the desktop app; no duplicate live cache is retained.
            var quota = await probe.VerifyAsync(runtime.Home, identity, token);
            if (quota.ServerAccountId != identity.AccountId || quota.Buckets.Count == 0)
                throw new InvalidDataException("官方核验返回的账号或额度信息不完整，已停止启动。");
            runtime.RequireStopped();
            var refreshed = auth.Read();
            try
            {
                if (!DesktopLogin.Inspect(refreshed).Matches(identity)) throw new InvalidDataException("核验后账号发生变化，已停止启动。");
                vault.Save(target.Id, refreshed);
                journal = journal with { Stage = "verified", InstalledHash = DesktopLogin.Fingerprint(refreshed) };
                vault.SavePending(journal);
            }
            finally { Clear(refreshed); }
            if (!quota.Buckets.TryGetValue(pool, out var bucket))
                throw new InvalidDataException("官方未返回当前额度池的信息，已停止启动。请先核验账号额度。");
            if (quota.OrdinaryUsageAllowed == false || !string.IsNullOrEmpty(bucket.ReachedType)
                || bucket.SpendControlReached == true || bucket.IndividualRemaining is <= 0
                || bucket.ForDuration(300)?.Remaining <= 0 || bucket.ForDuration(10080)?.Remaining <= 0)
                throw new InvalidOperationException("目标账号当前不可用或额度已用完，未打开 Codex，请选择其他账号。");
            token.ThrowIfCancellationRequested();
            runtime.RequireStopped();
            journal = journal with { Stage = "launching" }; vault.SavePending(journal);
            SetMessage("登录已核验，正在打开 Codex…");
            await runtime.LaunchAsync(token);
            var launched = auth.Read();
            try
            {
                if (!DesktopLogin.Inspect(launched).Matches(identity)) throw new InvalidDataException("启动后登录状态发生变化，请退出 Codex 后恢复。");
                vault.Save(target.Id, launched);
            }
            finally { Clear(launched); }
            vault.Complete();
            SetMessage($"已为「{target.Name}」打开 Codex。请核对 Codex 中显示的账号。");
        }
        catch (Exception error)
        {
            var restored = journal is null;
            if (journal is not null)
            {
                try { RestoreCore(journal); restored = true; }
                catch { /* Keep the encrypted journal; never overwrite an active or unrecognized login. */ }
            }
            var reason = error switch
            {
                OperationCanceledException => "操作已取消或核验超时。",
                CodexRpcException => "目标登录未通过官方核验，请检查网络或在 Codex 重新登录并保存。",
                InvalidDataException or InvalidOperationException => error.Message,
                IOException => "登录文件无法读写，请检查目录权限或重新保存桌面登录。",
                _ => "切换未完成。"
            };
            SetMessage(restored ? reason + (journal is null ? "" : " 已恢复切换前的本地登录。")
                : "切换未完成，恢复记录已保留。请先从托盘退出 Codex，再点击“恢复上次登录”。");
            // Do not propagate backend responses, token strings, paths or inner exceptions into the UI.
            throw new InvalidOperationException(Message);
        }
        finally { Clear(original); Clear(replacement); gate.Release(); }
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("已有切换操作正在进行。");
        SwitchJournal? journal = null;
        try
        {
            runtime.RequireStopped();
            journal = vault.LoadPending();
            if (journal is null) { SetMessage("没有需要恢复的切换。"); return; }
            RestoreCore(journal);
            SetMessage("已恢复切换前的本地登录。可以重新打开 Codex。");
        }
        finally { Clear(journal?.Original); gate.Release(); }
    }

    public async Task KeepCurrentAsync(IReadOnlyList<AccountRecord> accounts, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("已有切换操作正在进行。");
        SwitchJournal? journal = null;
        byte[]? current = null;
        try
        {
            runtime.RequireStopped();
            journal = vault.LoadPending();
            if (journal is null) { SetMessage("没有需要处理的切换。"); return; }
            RequireJournalHome(journal);
            current = auth.Read();
            var identity = DesktopLogin.Inspect(current);
            var again = auth.Read();
            try { if (!DesktopLogin.Same(current, again)) throw new IOException("登录状态正在更新，请稍后重试。"); }
            finally { Clear(again); }
            runtime.RequireStopped();
            token.ThrowIfCancellationRequested();
            foreach (var account in accounts.Where(a => MatchesRecord(identity, a))) vault.Save(account.Id, current);
            // Explicit recovery choice: keep the login file exactly as it is and release
            // the pending transaction, even when the user has since logged into account C.
            vault.Complete();
            SetMessage("已保留当前 Codex 登录，可以继续保存或切换账号。");
        }
        finally { Clear(current); Clear(journal?.Original); gate.Release(); }
    }

    private void RestoreCore(SwitchJournal journal)
    {
        RequireJournalHome(journal);
        runtime.RequireStopped();
        var current = auth.Read();
        try
        {
            if (DesktopLogin.Same(current, journal.Original)) { vault.Complete(); return; }
            // A prepared journal may predate a failed compare-and-replace. Only our exact
            // replacement is recoverable in that stage; a later login must be preserved.
            if (journal.Stage == "prepared" && DesktopLogin.Fingerprint(current) != journal.InstalledHash)
                throw new InvalidDataException("登录状态在切换前发生了变化，未覆盖新的登录。");
            if (!DesktopLogin.Inspect(current).Matches(journal.Target))
                throw new InvalidDataException("当前登录不是此次切换目标，未覆盖新的登录。");
            // Preserve even a rotated target token if the switch later needs to roll back.
            vault.Save(journal.AccountRecordId, current);
            if (DesktopLogin.Inspect(journal.Original).Matches(journal.Target)) { vault.Complete(); return; }
            runtime.RequireStopped();
            auth.Replace(journal.Original, DesktopLogin.Fingerprint(current));
            vault.Complete();
        }
        finally { Clear(current); }
    }

    private void RequireJournalHome(SwitchJournal journal)
    {
        if (!string.Equals(Path.GetFullPath(journal.Home), Path.GetFullPath(runtime.Home), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("登录目录与恢复记录不一致，未改写任何登录。");
    }

    public void RemoveSaved(AccountRecord account)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("请等待切换结束。");
        try { RequireNoPending(); vault.Remove(account.Id); }
        finally { gate.Release(); }
    }
    private void RequireNoPending() { if (vault.HasPending) throw new InvalidOperationException("请先处理未完成的切换：退出 Codex 后点击“恢复上次登录”。"); }
    private static bool MatchesRecord(DesktopIdentity identity, AccountRecord record)
    { try { DesktopLogin.MatchAccount(identity, record); return true; } catch (InvalidDataException) { return false; } }
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    private void SetMessage(string value) { Message = value; Changed?.Invoke(); }
}
