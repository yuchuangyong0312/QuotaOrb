using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuotaOrb.Services;

public sealed record SwitchJournal(int Version, string Home, string AccountRecordId,
    byte[] Original, DesktopIdentity Target, string InstalledHash, string Stage, bool IndependentTarget = false);

internal sealed record DesktopLoginSnapshot(int QuotaOrbSnapshotVersion, byte[] Login, bool PendingEnrollment);

// Separate from accounts.db: ordinary account export and software backup cannot include OAuth snapshots.
public sealed class DesktopLoginVault
{
    private readonly string root;
    public DesktopLoginVault(string dataRoot)
    {
        root = Path.Combine(Path.GetFullPath(dataRoot), "desktop-logins");
        SwitchFiles.PrivateDirectory(root);
    }
    private string Entry(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(root, id + ".bin") : throw new InvalidDataException("账号标识无效。");
    public bool Has(string id) => File.Exists(Entry(id));
    public bool HasPending => File.Exists(Path.Combine(root, "pending.bin"));
    public void Save(string id, byte[] bytes, bool pendingEnrollment = false)
    {
        DesktopLogin.Inspect(bytes);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new DesktopLoginSnapshot(2, bytes, pendingEnrollment));
        try { Write(Entry(id), serialized, "account/" + id); }
        finally { CryptographicOperations.ZeroMemory(serialized); }
    }
    public byte[] Load(string id) => LoadSnapshot(id).Login;
    internal DesktopLoginSnapshot LoadSnapshot(string id)
    {
        var bytes = Read(Entry(id), "account/" + id);
        byte[]? login = null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("QuotaOrbSnapshotVersion", out _))
            {
                DesktopLogin.Inspect(bytes);
                var legacy = new DesktopLoginSnapshot(2, bytes, false); bytes = []; return legacy;
            }
            var snapshot = JsonSerializer.Deserialize<DesktopLoginSnapshot>(bytes);
            login = snapshot?.Login;
            if (snapshot is null || snapshot.QuotaOrbSnapshotVersion != 2 || login is null)
                throw new InvalidDataException("桌面登录缓存格式不支持，请重新授权。");
            DesktopLogin.Inspect(login);
            login = null;
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (login is not null) CryptographicOperations.ZeroMemory(login); }
    }
    internal void SaveOutgoing(string id, byte[] bytes)
    {
        if (Has(id))
        {
            var saved = LoadSnapshot(id);
            try { if (saved.PendingEnrollment) return; }
            finally { CryptographicOperations.ZeroMemory(saved.Login); }
        }
        Save(id, bytes);
    }
    public void Remove(string id) { if (HasPending) throw new IOException("请先处理未完成的切换。"); var p = Entry(id); SwitchFiles.CheckPath(p); File.Delete(p); }
    public void SavePending(SwitchJournal journal)
    {
        Validate(journal);
        Entry(journal.AccountRecordId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        try { Write(Path.Combine(root, "pending.bin"), bytes, "pending/v1"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public SwitchJournal? LoadPending()
    {
        if (!HasPending) return null;
        var bytes = Read(Path.Combine(root, "pending.bin"), "pending/v1");
        try
        {
            var journal = JsonSerializer.Deserialize<SwitchJournal>(bytes) ?? throw new InvalidDataException("恢复记录不可用。");
            Validate(journal); Entry(journal.AccountRecordId); DesktopLogin.Inspect(journal.Original); return journal;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Complete() { var p = Path.Combine(root, "pending.bin"); SwitchFiles.CheckPath(p); File.Delete(p); }
    private static void Validate(SwitchJournal journal)
    {
        if (journal.Version != 1 || journal.Original is null || journal.Target is null
            || !Path.IsPathFullyQualified(journal.Home) || !Guid.TryParseExact(journal.AccountRecordId, "N", out _)
            || journal.Stage is not ("prepared" or "installed" or "verified" or "launching")
            || journal.InstalledHash is null || journal.InstalledHash.Length != 64)
            throw new InvalidDataException("恢复记录不支持，未改写 Codex 登录。");
        DesktopLogin.Inspect(journal.Original);
    }
    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("QuotaOrb/desktop-login/v1/" + purpose);
    private static void Write(string path, byte[] bytes, string purpose)
    {
        var encrypted = ProtectedData.Protect(bytes, Entropy(purpose), DataProtectionScope.CurrentUser);
        SwitchFiles.AtomicWrite(path, encrypted);
    }
    private static byte[] Read(string path, string purpose)
    {
        var encrypted = SwitchFiles.Read(path, 2 * DesktopLogin.MaxBytes);
        try { return ProtectedData.Unprotect(encrypted, Entropy(purpose), DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidDataException("桌面登录状态无法在当前 Windows 用户下解密，请重新保存。"); }
    }
}
