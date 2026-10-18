using QuotaOrb.Services;

internal static class CleanupChecks
{
    public static async Task RunAsync(string root, Action<string, bool> check)
    {
        // Codex may leave a read-only Git pack in its temporary plugin clone.
        // A restart must clean that owned attempt without losing saved logins.
        foreach (var component in new[] { "desktop-enrollment", "desktop-checks" })
        {
            var f = new Fixture(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var data = Path.Combine(f.Root, "data");
            var parent = Path.Combine(data, component);
            var attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            MakeReadOnlyPack(attempt);
            var unrelated = Path.Combine(parent, "unrecognized");
            var keep = MakeReadOnlyPack(unrelated);
            var saved = Directory.GetFiles(Path.Combine(data, "desktop-logins")).ToDictionary(p => p, File.ReadAllBytes);
            Exception? failure = null;
            try
            {
                if (component == "desktop-enrollment") _ = new DesktopLoginEnrollment(data, _ => throw new Exception("No network during startup"));
                else _ = new DesktopAuthorizationProbe(data, _ => throw new Exception("No network during startup"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure = error; }
            check("restart cleans read-only plugin residue: " + component, failure is null && !Directory.Exists(attempt));
            check("startup cleanup preserves unknown folders and encrypted logins: " + component,
                File.ReadAllText(keep) == "synthetic plugin pack" && File.GetAttributes(keep).HasFlag(FileAttributes.ReadOnly)
                && saved.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && f.Current().SequenceEqual(f.Original));
        }
        {
            var f = new Fixture(Path.Combine(root, Guid.NewGuid().ToString("N")));
            var data = Path.Combine(f.Root, "data");
            var enrollment = new DesktopLoginEnrollment(data, home => new FakeEnrollmentSession(home, "b")
            { OnDispose = () => MakeReadOnlyPack(home) });
            var switcher = new DesktopSwitcher(data, f.Runtime, f.Probe, enrollment);
            var result = await switcher.AuthorizeAsync(f.B, LoginMethod.Browser, _ => { }, default);
            check("successful enrollment removes read-only plugin residue before saving authorization",
                result.Success && switcher.HasAuthorized(f.B) && f.Vault.Load(f.B.Id).SequenceEqual(Fixture.Login("b", "enrolled"))
                && !Directory.EnumerateDirectories(Path.Combine(data, "desktop-enrollment")).Any() && f.Current().SequenceEqual(f.Original));
        }
        {
            var parent = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var attempt = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var pack = MakeReadOnlyPack(attempt);
            SwitchFiles.DeletePrivateAttempt(parent, attempt);
            check("shared attempt cleanup deletes nested read-only files", !Directory.Exists(attempt));
            pack = MakeReadOnlyPack(attempt);
            using (var locked = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var rejected = false;
                try { SwitchFiles.DeletePrivateAttempt(parent, attempt); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                check("a genuinely locked temporary file still reports cleanup failure", rejected && File.Exists(pack));
            }
            SwitchFiles.DeletePrivateAttempt(parent, attempt);
            check("cleanup can recover after the file lock is released", !Directory.Exists(attempt));
            foreach (var candidate in new[] { Path.Combine(root, Guid.NewGuid().ToString("N")), Path.Combine(parent, "unknown"), Path.Combine(parent, "nested", Guid.NewGuid().ToString("N")) })
            {
                pack = MakeReadOnlyPack(candidate);
                var rejected = false;
                try { SwitchFiles.DeletePrivateAttempt(parent, candidate); }
                catch (IOException) { rejected = true; }
                check("cleanup rejects an unowned directory before changing attributes: " + Path.GetRelativePath(parent, candidate),
                    rejected && File.ReadAllText(pack) == "synthetic plugin pack" && File.GetAttributes(pack).HasFlag(FileAttributes.ReadOnly));
            }
        }
    }

    private static string MakeReadOnlyPack(string attempt)
    {
        var directory = Path.Combine(attempt, ".tmp", "plugins-clone-synthetic", ".git", "objects", "pack");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "tmp_pack_synthetic");
        File.WriteAllText(file, "synthetic plugin pack");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        return file;
    }
}
