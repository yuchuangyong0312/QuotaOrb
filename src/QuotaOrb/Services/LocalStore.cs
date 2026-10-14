using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace QuotaOrb.Services;

public sealed class LocalStore : IDisposable
{
    public string Root { get; }
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public bool PendingPrivacyWork { get; private set; }
    public bool PendingDataUpgrade { get; private set; }
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    public LocalStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "accounts.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; CREATE TABLE IF NOT EXISTS app_state (id INTEGER PRIMARY KEY CHECK (id=1), version INTEGER NOT NULL, json TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public AppData Load()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM app_state WHERE id=1";
            var raw = command.ExecuteScalar() as string;
            if (raw is null) return new();
            var data = JsonSerializer.Deserialize<AppData>(raw, Json) ?? throw new InvalidDataException("本地数据无法读取，原文件已保留。");
            Validate(data);
            PendingPrivacyWork |= data.Version == 1 || data.NotesCleanupPending || data.Accounts.Any(a => !string.IsNullOrEmpty(a.Notes));
            foreach (var account in data.Accounts.Where(a => a.ProtectedNotes is not null))
            {
                try { account.Notes = SecretVault.RevealNotes(account.ProtectedNotes); }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // Keep the ciphertext even when a different Windows user cannot decrypt it.
                    account.Notes = ""; account.NotesUnavailable = true;
                }
            }
            Validate(data);
            PendingDataUpgrade |= data.Version < 3;
            foreach (var account in data.Accounts) PendingDataUpgrade |= MigrateLoginInfo(account);
            if (PendingPrivacyWork || PendingDataUpgrade)
            {
                try { Save(data); }
                catch (Exception ex) when (IsStorageFailure(ex)) { /* Retry through the normal save flow; keep all data in memory. */ }
            }
            return data;
        }
    }
    public void Save(AppData data)
    {
        lock (gate)
        {
            Validate(data);
            var copy = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data, Json), Json)!;
            copy.Version = 3;
            copy.NotesCleanupPending = PendingPrivacyWork;
            for (var i = 0; i < data.Accounts.Count; i++)
            {
                var account = data.Accounts[i];
                copy.Accounts[i].ProtectedNotes = account.NotesUnavailable ? account.ProtectedNotes : SecretVault.ProtectNotes(account.Notes);
                copy.Accounts[i].Notes = "";
            }
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_state(id,version,json) VALUES(1,3,$json) ON CONFLICT(id) DO UPDATE SET version=3,json=$json";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(copy, Json));
            command.ExecuteNonQuery();
            PendingDataUpgrade = false;
            data.Version = copy.Version; data.NotesCleanupPending = copy.NotesCleanupPending;
            for (var i = 0; i < data.Accounts.Count; i++) data.Accounts[i].ProtectedNotes = copy.Accounts[i].ProtectedNotes;
            if (PendingPrivacyWork) TryClearLegacyPages(data, copy);
        }
    }
    private void TryClearLegacyPages(AppData data, AppData stored)
    {
        try
        {
            if (!Checkpoint()) return;
            using var command = connection.CreateCommand(); command.CommandText = "VACUUM"; command.ExecuteNonQuery();
            if (!Checkpoint()) return;
            stored.NotesCleanupPending = false;
            using var clearMarker = connection.CreateCommand(); clearMarker.CommandText = "UPDATE app_state SET json=$json WHERE id=1";
            clearMarker.Parameters.AddWithValue("$json", JsonSerializer.Serialize(stored, Json)); clearMarker.ExecuteNonQuery();
            data.NotesCleanupPending = false; PendingPrivacyWork = false;
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { /* The encrypted current record is saved; old-page cleanup remains pending. */ }
    }
    private bool Checkpoint()
    {
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader(); return reader.Read() && reader.GetInt32(0) == 0;
    }
    public static bool IsStorageFailure(Exception error) => error is SqliteException or IOException or UnauthorizedAccessException or CryptographicException;
    public static AccountRecord CopyAccount(AccountRecord account)
    {
        var copy = JsonSerializer.Deserialize<AccountRecord>(JsonSerializer.Serialize(account, Json), Json)!;
        copy.NotesUnavailable = account.NotesUnavailable;
        return copy;
    }
    internal static bool MigrateLoginInfo(AccountRecord account)
    {
        if (account.LoginInfoInitialized || account.NotesUnavailable) return false;
        var lines = account.Notes.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Only the unambiguous legacy URL + Base32 key layout is migrated. Keep the original encrypted note.
        if (lines.Length == 2 && Uri.TryCreate(lines[0], UriKind.Absolute, out var url) && url.Scheme is "https" or "http" &&
            System.Text.RegularExpressions.Regex.IsMatch(lines[1], "^[A-Za-z2-7]{16,128}=*$"))
        {
            account.ProtectedTwoFactorUrl ??= SecretVault.ProtectTwoFactorUrl(lines[0]);
            account.ProtectedTwoFactorKey ??= SecretVault.ProtectTwoFactorKey(lines[1]);
        }
        account.LoginInfoInitialized = true;
        return true;
    }
    public static void Validate(AppData data)
    {
        if (data.Version is not (1 or 2 or 3) || data.Accounts is null || data.Settings is null || data.Accounts.Count > 500) throw new InvalidDataException("不支持的数据文件版本或账号数量。");
        if (data.Accounts.Any(a => a is null || !Guid.TryParseExact(a.Id, "N", out _) || a.Name is null || a.History is null || a.Gaps is null) || data.Accounts.Select(a => a.Id).Distinct().Count() != data.Accounts.Count) throw new InvalidDataException("账号数据不完整。");
        foreach (var a in data.Accounts)
        {
            if (a.Email is null || a.Notes is null || a.Plan is null || a.Name.Length>60 || a.Email.Length>254 || a.Notes.Length>4000 || a.ProtectedNotes?.Length>32768 || a.ProtectedPassword?.Length>32768 || a.History.Count>1000 || a.Gaps.Count>1000 || a.History.Any(h=>h is null || h.Key is null || h.LimitId is null) || a.Gaps.Any(g=>g is null)) throw new InvalidDataException("账号字段内容无效。");
            if (a.ProtectedTwoFactorUrl?.Length>32768 || a.ProtectedTwoFactorKey?.Length>32768) throw new InvalidDataException("登录信息字段过长。");
            if (a.Snapshot is { } s && (s.Buckets is null || s.Buckets.Count>100 || s.Buckets.Any(p=>string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Id!=p.Key) || s.ResetCount<0 || s.ResetCredits?.Any(c=>c is null)==true)) throw new InvalidDataException("额度快照内容无效。");
        }
        if (string.IsNullOrWhiteSpace(data.Settings.LimitId)) throw new InvalidDataException("查询设置无效。");
    }
    public string ProfilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效的账号标识");
        return Path.Combine(Root, "profiles", id);
    }
    public void ExportMetadata(AppData data, string destination, bool includeNotes = false)
    {
        Validate(data);
        var copy = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data, Json), Json)!;
        copy.Version = 3; copy.NotesCleanupPending = false;
        foreach (var account in copy.Accounts)
        {
            account.ProtectedPassword = null; account.ProtectedNotes = null; account.AuthState = "unlinked"; account.LastError = null;
            account.ProtectedTwoFactorUrl = null; account.ProtectedTwoFactorKey = null;
            if (!includeNotes) account.Notes = "";
        }
        File.WriteAllText(destination, JsonSerializer.Serialize(copy, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false));
    }
    public void ExportAll(AppData data, string destination)
    {
        Validate(data);
        var copy = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data, Json), Json)!;
        copy.Version = 3; copy.NotesCleanupPending = false;
        var export = new AccountExport { Data = copy };
        for (var i = 0; i < data.Accounts.Count; i++)
        {
            var original = data.Accounts[i]; var account = copy.Accounts[i];
            if (original.NotesUnavailable) throw new InvalidDataException("有账号的旧备注无法解密，完整导出未完成。请使用原 Windows 用户打开后重试。");
            export.LoginInfo.Add(account.Id, new ExportLoginInfo
            {
                Password = SecretVault.Reveal(original.ProtectedPassword),
                TwoFactorUrl = SecretVault.RevealTwoFactorUrl(original.ProtectedTwoFactorUrl),
                TwoFactorKey = SecretVault.RevealTwoFactorKey(original.ProtectedTwoFactorKey)
            });
            account.ProtectedPassword = null; account.ProtectedNotes = null; account.ProtectedTwoFactorUrl = null; account.ProtectedTwoFactorKey = null;
            account.AuthState = "unlinked"; account.LastError = null;
        }
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions(Json) { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > 20 * 1024 * 1024) throw new InvalidDataException("导出内容超过 20 MB，请减少历史数据后重试。");
        var target = Path.GetFullPath(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".quota-orb-export-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public List<AccountRecord> ImportMetadata(string source)
    {
        if (new FileInfo(source).Length > 20 * 1024 * 1024) throw new InvalidDataException("备份文件过大。");
        var text = File.ReadAllText(source);
        using var document = JsonDocument.Parse(text);
        var complete = document.RootElement.TryGetProperty("Format", out _) ? JsonSerializer.Deserialize<AccountExport>(text, Json) : null;
        if (complete is not null && (complete.Format != AccountExport.FormatName || complete.Version != 1 || complete.Data is null || complete.LoginInfo is null)) throw new InvalidDataException("完整备份格式不支持。");
        var imported = complete?.Data ?? JsonSerializer.Deserialize<AppData>(text, Json) ?? throw new InvalidDataException("备份内容无效。");
        Validate(imported);
        if (complete is not null && (complete.LoginInfo.Count != imported.Accounts.Count || imported.Accounts.Any(a => !complete.LoginInfo.TryGetValue(a.Id, out var login) || login is null || login.Password is null || login.TwoFactorUrl is null || login.TwoFactorKey is null || login.Password.Length>4096 || login.TwoFactorUrl.Length>4096 || login.TwoFactorKey.Length>4096))) throw new InvalidDataException("备份登录信息不完整或字段过长。");
        imported.NotesCleanupPending = false;
        foreach (var account in imported.Accounts)
        {
            var login = complete?.LoginInfo[account.Id];
            // Imported profile IDs never gain access to existing local credentials.
            account.Id = Guid.NewGuid().ToString("N"); account.ProtectedPassword = null; account.ProtectedNotes = null; account.AuthState = "unlinked"; account.LastError = null;
            account.ProtectedTwoFactorUrl = null; account.ProtectedTwoFactorKey = null;
            if (login is not null)
            {
                account.ProtectedPassword = SecretVault.Protect(login.Password);
                account.ProtectedTwoFactorUrl = SecretVault.ProtectTwoFactorUrl(login.TwoFactorUrl);
                account.ProtectedTwoFactorKey = SecretVault.ProtectTwoFactorKey(login.TwoFactorKey);
                account.LoginInfoInitialized = true;
            }
            else MigrateLoginInfo(account);
        }
        return imported.Accounts;
    }
    public void Dispose() => connection.Dispose();
}

public sealed class AccountExport
{
    public const string FormatName = "QuotaOrb.account-backup";
    [System.Text.Json.Serialization.JsonRequired] public string Format { get; set; } = FormatName;
    [System.Text.Json.Serialization.JsonRequired] public int Version { get; set; } = 1;
    [System.Text.Json.Serialization.JsonRequired] public AppData Data { get; set; } = new();
    [System.Text.Json.Serialization.JsonRequired] public Dictionary<string,ExportLoginInfo> LoginInfo { get; set; } = [];
}
public sealed class ExportLoginInfo
{
    [System.Text.Json.Serialization.JsonRequired] public string Password { get; set; } = "";
    [System.Text.Json.Serialization.JsonRequired] public string TwoFactorUrl { get; set; } = "";
    [System.Text.Json.Serialization.JsonRequired] public string TwoFactorKey { get; set; } = "";
}

public static class SecretVault
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("QuotaOrb/password/v1");
    private static readonly byte[] NotesEntropy = Encoding.UTF8.GetBytes("QuotaOrb/notes/v1");
    private static readonly byte[] TwoFactorUrlEntropy = Encoding.UTF8.GetBytes("QuotaOrb/two-factor-url/v1");
    private static readonly byte[] TwoFactorKeyEntropy = Encoding.UTF8.GetBytes("QuotaOrb/two-factor-key/v1");
    public static string? Protect(string value) => Protect(value, PasswordEntropy);
    public static string Reveal(string? value) => Reveal(value, PasswordEntropy);
    public static string? ProtectNotes(string value) => Protect(value, NotesEntropy);
    public static string RevealNotes(string? value) => Reveal(value, NotesEntropy);
    public static string? ProtectTwoFactorUrl(string value) => Protect(value, TwoFactorUrlEntropy);
    public static string RevealTwoFactorUrl(string? value) => Reveal(value, TwoFactorUrlEntropy);
    public static string? ProtectTwoFactorKey(string value) => Protect(value, TwoFactorKeyEntropy);
    public static string RevealTwoFactorKey(string? value) => Reveal(value, TwoFactorKeyEntropy);
    private static string? Protect(string value, byte[] entropy)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Reveal(string? value, byte[] entropy)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
