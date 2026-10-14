using System.Security.Cryptography;
using System.Text.Json;

namespace QuotaOrb.Services;

// Claims are a local consistency check, NOT proof of server authentication.
// The desktop probe must contact the official backend before a switch is committed.
public sealed record DesktopIdentity(string Email, string UserId, string AccountId)
{
    public bool Matches(DesktopIdentity other) =>
        string.Equals(Email, other.Email, StringComparison.OrdinalIgnoreCase)
        && UserId == other.UserId && AccountId == other.AccountId;
}

public static class DesktopLogin
{
    public const int MaxBytes = 512 * 1024;
    private const string AuthClaim = "https://api.openai.com/auth";
    private const string ProfileClaim = "https://api.openai.com/profile";

    public static DesktopIdentity Inspect(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new FormatException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            EnsureUnique(root);
            if (Text(root, "auth_mode") != "chatgpt"
                || (root.TryGetProperty("OPENAI_API_KEY", out var apiKey) && apiKey.ValueKind != JsonValueKind.Null))
                throw new FormatException();
            var tokens = root.GetProperty("tokens");
            Required(tokens, "refresh_token");
            var account = Required(tokens, "account_id");
            using var access = Claims(Required(tokens, "access_token"));
            using var id = Claims(Required(tokens, "id_token"));
            var a = access.RootElement; var i = id.RootElement;
            var auth = a.GetProperty(AuthClaim);
            if (Required(auth, "chatgpt_account_id") != account) throw new FormatException();
            var user = Required(auth, "chatgpt_user_id");
            var idAuth = i.GetProperty(AuthClaim);
            if (Required(idAuth, "chatgpt_user_id") != user || Required(idAuth, "chatgpt_account_id") != account)
                throw new FormatException();
            var email = Required(i, "email");
            if (!email.Contains('@') || email.Length > 320) throw new FormatException();
            if (a.TryGetProperty(ProfileClaim, out var profile) && Text(profile, "email") is { } accessEmail
                && !string.Equals(email, accessEmail, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
            return new(email, user, account);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new InvalidDataException("桌面登录文件格式不支持或账号信息不一致，请在 Codex 中重新登录后保存。"); }
    }

    public static void MatchAccount(DesktopIdentity identity, AccountRecord account)
    {
        if (!string.Equals(identity.Email, account.Email.Trim(), StringComparison.OrdinalIgnoreCase)
            || (account.Snapshot?.ServerAccountId is { Length: > 0 } serverId && identity.AccountId != serverId))
            throw new InvalidDataException("桌面当前登录与所选账号不一致，未保存或切换任何账号。");
    }

    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static bool Same(byte[] first, byte[] second) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(first), SHA256.HashData(second));

    private static JsonDocument Claims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        var bytes = Convert.FromBase64String(payload);
        try
        {
            // Parse(Stream) owns a copy; Parse(byte[]) would reference the buffer cleared below.
            using var stream = new MemoryStream(bytes, false);
            var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            try { EnsureUnique(doc.RootElement); return doc; }
            catch { doc.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Required(JsonElement e, string name) => Text(e, name) is { Length: > 0 } value ? value : throw new FormatException();
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static void EnsureUnique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new FormatException(); EnsureUnique(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) EnsureUnique(value);
    }
}
