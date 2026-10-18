using System.Security.AccessControl;
using System.Security.Principal;

namespace QuotaOrb.Services;

internal static class SwitchFiles
{
    internal static void DeletePrivateAttempt(string root, string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("临时检查目录无法验证。");
        CheckPath(full);
        if (!Directory.Exists(full)) return;
        var files = new List<string>();
        void CheckTree(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                CheckPath(entry);
                if (Directory.Exists(entry)) CheckTree(entry);
                else files.Add(entry);
            }
        }
        CheckTree(full);
        // Official plugin downloads can leave read-only Git pack files behind.
        // Validate the entire owned tree before changing any file attributes.
        foreach (var file in files)
        {
            CheckPath(file);
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(full, true);
    }

    public static void CheckPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("登录存储路径包含链接，已停止操作。");
    }

    public static void PrivateDirectory(string path)
    {
        CheckPath(path);
        Directory.CreateDirectory(path);
        CheckPath(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确认 Windows 用户。");
        acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    public static byte[] Read(string path, int max = DesktopLogin.MaxBytes)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > max) throw new InvalidDataException("登录文件大小异常。");
        var result = new byte[checked((int)stream.Length)]; stream.ReadExactly(result); return result;
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        CheckPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Restrict the temporary file before putting any credentials into it.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
                var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("无法确认 Windows 用户。");
                acl.SetOwner(sid); acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(temporary).SetAccessControl(acl);
                stream.Write(bytes); stream.Flush(true);
            }
            CheckPath(path);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class DesktopAuthFile
{
    public string Home { get; }
    private string AuthPath => Path.Combine(Home, "auth.json");
    public DesktopAuthFile(string home) => Home = Path.GetFullPath(home);

    public byte[] Read()
    {
        SwitchFiles.CheckPath(Home);
        // Refuse mixed/new encrypted storage rather than guessing credential precedence.
        if (File.Exists(Path.Combine(Home, "secrets", "codex_auth.age")))
            throw new IOException("此 Codex 使用加密登录存储，暂不支持切换。");
        var config = Path.Combine(Home, "config.toml");
        if (File.Exists(config))
        {
            var text = File.ReadAllText(config);
            var entries = System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^\s*cli_auth_credentials_store\s*=\s*([^\r\n#]+)");
            if (entries.Count > 1 || (entries.Count == 1 && entries[0].Groups[1].Value.Trim() is not "\"file\"" and not "'file'"))
                throw new IOException("此 Codex 登录存储配置暂不支持切换，未修改配置。");
        }
        var bytes = SwitchFiles.Read(AuthPath);
        try { DesktopLogin.Inspect(bytes); return bytes; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    public void Replace(byte[] replacement, string expectedHash)
    {
        DesktopLogin.Inspect(replacement);
        var current = Read();
        try { if (DesktopLogin.Fingerprint(current) != expectedHash) throw new IOException("Codex 登录状态刚刚发生变化，请重新操作。"); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(current); }
        SwitchFiles.AtomicWrite(AuthPath, replacement);
    }
}
