using System.Reflection;
using System.Text.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using QuotaOrb.Services;

var root = Path.GetFullPath(args.Length == 1 ? args[0] : Path.Combine(Path.GetTempPath(), "quota-orb-service-check-" + Guid.NewGuid().ToString("N")));
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new, isolated test directory.");
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
SqliteConnection Connection(LocalStore store) => (SqliteConnection)typeof(LocalStore).GetField("connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
void Execute(LocalStore store, string sql) { using var command = Connection(store).CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
string ReadShared(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var memory = new MemoryStream(); stream.CopyTo(memory); return Encoding.UTF8.GetString(memory.ToArray());
}
void SeedLegacy(LocalStore store, AppData data)
{
    data.Version = 1;
    using var command = Connection(store).CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO app_state(id,version,json) VALUES(1,1,$json)";
    command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(data, LocalStore.Json)); command.ExecuteNonQuery();
}
AccountRecord Make(bool ready = true)
{
    var account = new AccountRecord { Name = "Synthetic account", AuthState = ready ? "ready" : "unlinked", Notes = "Synthetic-note-only" };
    account.Email = account.Id + "@example.invalid";
    return account;
}
var fakeBin = Path.Combine(root, "FakeBin"); Directory.CreateDirectory(fakeBin); File.WriteAllText(Path.Combine(fakeBin, "codex.exe"), "Never executed: synthetic resolver fixture.");
var installation = new CodexInstallation([fakeBin], () => null);
var missing = new CodexInstallation([], () => null);
try
{
    await LoginFlowChecks.RunAsync(Check);
    using (var store = new LocalStore(Path.Combine(root, "Batch")))
    using (var service = new AccountService(store, installation))
    {
        CodexClient.Reset(); var a = Make(); var b = Make(); service.Data.Accounts.AddRange([a, b]); service.Save();
        Task? duplicate = null, single = null, deletion = null, login = null;
        var gapObserved = false; var editBlocked = false;
        service.Changed += () =>
        {
            if (gapObserved || !service.Busy || service.BusyAccountId is not null || CodexClient.Queries.Count != 1) return;
            gapObserved = true;
            duplicate = service.RefreshAllAsync(true); single = service.RefreshAsync(a); deletion = service.RemoveAsync(a); login = service.LoginAsync(a, CancellationToken.None);
            try { service.AddAccount(Make(false)); } catch (InvalidOperationException) { editBlocked = true; }
        };
        await service.RefreshAllAsync(true); await duplicate!; await single!;
        var deletionBlocked = false; var loginBlocked = false;
        try { await deletion!; } catch (InvalidOperationException) { deletionBlocked = true; }
        try { await login!; } catch (InvalidOperationException) { loginBlocked = true; }
        Check("batch remains busy between account queries", gapObserved && !service.Busy);
        Check("a second full refresh cannot query accounts twice", CodexClient.Queries[a.Id] == 1 && CodexClient.Queries[b.Id] == 1);
        Check("single refresh, login, deletion and edits cannot interrupt a batch", editBlocked && deletionBlocked && loginBlocked && CodexClient.Logouts == 0 && service.Data.Accounts.Count == 2);
        var edited = LocalStore.CopyAccount(a); edited.Name = "Edited metadata"; edited.Snapshot = null;
        service.UpdateAccount(edited);
        Check("metadata edit preserves the latest quota snapshot", service.Data.Accounts[0].Name == edited.Name && JsonSerializer.Serialize(service.Data.Accounts[0].Snapshot) == JsonSerializer.Serialize(a.Snapshot));
    }
    using (var store = new LocalStore(Path.Combine(root, "WriteFault")))
    using (var service = new AccountService(store, installation))
    {
        CodexClient.Reset(); var a = Make(); service.AddAccount(a); var diskBefore = store.Load();
        var notices = 0; service.Notice += (title, _) => { if (title == "数据尚未保存") notices++; };
        Execute(store, "PRAGMA query_only=ON");
        await service.RefreshAsync(a);
        Check("read-only SQLite never escapes quota refresh", !service.Busy && a.Snapshot is not null && a.LastError is null);
        Check("write failure retains fresh quota in memory with a visible unsaved state", service.HasUnsavedChanges && service.StorageError is not null && store.Load().Accounts[0].Snapshot == diskBefore.Accounts[0].Snapshot);
        Check("manual retry reports failure without throwing", !service.Save() && notices == 1);
        var draft = Make(false); var failed = false;
        try { service.AddAccount(draft); } catch (IOException) { failed = true; }
        Check("failed addition is rolled back", failed && service.Data.Accounts.Count == 1 && store.Load().Accounts.Count == 1);
        var edit = LocalStore.CopyAccount(a); edit.Name = "Must not commit"; failed = false;
        try { service.UpdateAccount(edit); } catch (IOException) { failed = true; }
        Check("failed edit preserves the original metadata", failed && service.Data.Accounts[0].Name == a.Name);
        var originalSettings = service.Data.Settings; failed = false;
        try { service.UpdateSettings(new() { AutoRefresh = false, Notifications = false }); } catch (IOException) { failed = true; }
        Check("failed settings save is rolled back", failed && service.Data.Settings == originalSettings);
        failed = false; try { service.ImportAccounts([Make(false), Make(false)]); } catch (IOException) { failed = true; }
        Check("failed import leaves no partial account list", failed && service.Data.Accounts.Count == 1);
        Execute(store, "PRAGMA query_only=OFF");
        Check("retry commits retained quota after the fault clears", service.Save() && !service.HasUnsavedChanges && service.StorageError is null && store.Load().Accounts[0].LastSuccessUtc == a.LastSuccessUtc);
        service.AddAccount(draft); Execute(store, "PRAGMA query_only=ON"); failed = false;
        try { await service.RemoveAsync(draft); } catch (IOException) { failed = true; }
        Check("failed local deletion keeps the draft and never invokes logout", failed && service.Data.Accounts.Contains(draft) && CodexClient.Logouts == 0);
        failed = false; try { await service.RemoveAsync(a); } catch (IOException) { failed = true; }
        Check("logout followed by storage failure retains an explicitly unlinked account", failed && service.Data.Accounts.Contains(a) && a.AuthState == "unlinked" && service.HasUnsavedChanges && CodexClient.Logouts == 1);
        Execute(store, "PRAGMA query_only=OFF"); service.Save();
        Check("retry persists the actual logout state", store.Load().Accounts.First(x => x.Id == a.Id).AuthState == "unlinked");
    }
    using (var store = new LocalStore(Path.Combine(root, "NoCodex")))
    using (var service = new AccountService(store, missing))
    {
        CodexClient.Reset(); var draft = Make(false); var linked = Make(); var profileDraft = Make(false);
        service.Data.Accounts.AddRange([draft, linked, profileDraft]); service.Save(); Directory.CreateDirectory(store.ProfilePath(profileDraft.Id));
        await service.RemoveAsync(draft);
        Check("a never-authorized draft can be deleted without installed Codex", service.Data.Accounts.Count == 2 && CodexClient.Logouts == 0);
        var blocked = false; try { await service.RemoveAsync(linked); } catch (FileNotFoundException) { blocked = true; }
        Check("previously linked accounts are retained when logout is unavailable", blocked && service.Data.Accounts.Contains(linked) && !service.Busy);
        blocked = false; try { await service.RemoveAsync(profileDraft); } catch (FileNotFoundException) { blocked = true; }
        Check("a draft with a credential profile is treated conservatively", blocked && service.Data.Accounts.Contains(profileDraft));
        service.Data.Accounts.Clear(); service.Data.Accounts.AddRange(Enumerable.Range(0, 500).Select(_ => new AccountRecord()));
        blocked = false; try { service.AddAccount(new()); } catch (InvalidOperationException) { blocked = true; }
        Check("the 500-account limit is enforced before mutation", blocked && service.Data.Accounts.Count == 500);
    }
    using (var store = new LocalStore(Path.Combine(root, "NotesMigration")))
    {
        const string marker = "SYNTHETIC_OLD_PRIVATE_NOTE_7e54f8"; var a = Make(false); a.Notes = marker + " 中文备注";
        a.ProtectedPassword = SecretVault.Protect("Synthetic-password-only"); var originalPassword = a.ProtectedPassword;
        SeedLegacy(store, new() { Accounts = [a] }); var loaded = store.Load(); var current = loaded.Accounts[0];
        Check("legacy notes migrate automatically without losing text or passwords", current.Notes == a.Notes && current.ProtectedPassword == originalPassword && current.ProtectedNotes is not null && !store.PendingPrivacyWork);
        Check("encrypted storage upgrades its format so older programs refuse it", loaded.Version == 3 && !loaded.NotesCleanupPending);
        using (var command = Connection(store).CreateCommand())
        {
            command.CommandText = "SELECT json FROM app_state WHERE id=1"; var raw = (string)command.ExecuteScalar()!;
            Check("persisted JSON contains encrypted notes instead of plaintext", !raw.Contains(marker) && raw.Contains("ProtectedNotes"));
        }
        Check("migration clears the legacy marker from SQLite and WAL files", Directory.GetFiles(store.Root, "accounts.db*").All(path => !ReadShared(path).Contains(marker)));
        Check("encrypted notes can be loaded again", store.Load().Accounts[0].Notes == a.Notes);
        var exported = Path.Combine(root, "metadata.json"); store.ExportMetadata(loaded, exported);
        var rawExport = File.ReadAllText(exported);
        Check("default export excludes notes and all local ciphertext", !rawExport.Contains(marker) && !rawExport.Contains(current.ProtectedNotes!) && !rawExport.Contains(originalPassword!));
        var imported = store.ImportMetadata(exported);
        Check("metadata import uses new identities and no passwords or protected notes", imported[0].Id != a.Id && imported[0].AuthState == "unlinked" && imported[0].ProtectedNotes is null && imported[0].ProtectedPassword is null && imported[0].Notes == "");
        store.ExportMetadata(loaded, exported, includeNotes: true); imported = store.ImportMetadata(exported);
        Check("explicit note export preserves text without exporting Windows ciphertext", imported[0].Notes == a.Notes && imported[0].ProtectedNotes is null);
        var wrongPurposeRejected = false; try { SecretVault.Reveal(current.ProtectedNotes); } catch (System.Security.Cryptography.CryptographicException) { wrongPurposeRejected = true; }
        Check("notes and passwords use separate DPAPI purposes", wrongPurposeRejected);
        const string unreadable = "not-valid-base64-ciphertext";
        SeedLegacy(store, new() { Accounts = [new AccountRecord { Name = "Unreadable note fixture", ProtectedNotes = unreadable }] });
        loaded = store.Load(); current = loaded.Accounts[0];
        Check("unreadable notes are flagged without preventing startup", current.NotesUnavailable && current.Notes == "" && current.ProtectedNotes == unreadable);
        var copy = LocalStore.CopyAccount(current); copy.Name = "Edited other fields"; store.Save(new() { Accounts = [copy] });
        Check("editing other fields preserves unreadable ciphertext", store.Load().Accounts[0].ProtectedNotes == unreadable && store.Load().Accounts[0].NotesUnavailable);
        var malformedRejected = false; try { LocalStore.Validate(new() { Accounts = [null!] }); } catch (InvalidDataException) { malformedRejected = true; }
        Check("null account imports fail with a controlled validation error", malformedRejected);
    }
    using (var store = new LocalStore(Path.Combine(root, "MigrationWriteFault")))
    {
        var a = Make(false); a.Notes = "Synthetic-migration-write-fault-note"; SeedLegacy(store, new() { Accounts = [a] });
        Execute(store, "PRAGMA query_only=ON");
        using var service = new AccountService(store, missing);
        Check("failed privacy migration preserves notes and permits startup", service.Data.Accounts[0].Notes == a.Notes && service.StorageError is not null && service.HasUnsavedChanges);
        Check("failed privacy migration can be retried without throwing", !service.Save() && store.PendingPrivacyWork);
        Execute(store, "PRAGMA query_only=OFF");
        Check("privacy migration completes after storage recovers", service.Save() && !store.PendingPrivacyWork && service.StorageError is null && store.Load().Accounts[0].Notes == a.Notes);
    }
    var pendingRoot = Path.Combine(root, "PendingCleanupRestart");
    var pendingNote = "Synthetic-busy-reader-note";
    using (var store = new LocalStore(pendingRoot))
    {
        var a = Make(false); a.Notes = pendingNote; SeedLegacy(store, new() { Accounts = [a] }); Execute(store, "PRAGMA busy_timeout=50");
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(pendingRoot, "accounts.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        reader.Open(); using var transaction = reader.BeginTransaction(); using var command = reader.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT json FROM app_state WHERE id=1"; command.ExecuteScalar();
        var loaded = store.Load();
        Check("a busy reader does not undo encrypted storage", loaded.Version == 3 && loaded.Accounts[0].ProtectedNotes is not null && loaded.Accounts[0].Notes == pendingNote);
        Check("unfinished legacy-page cleanup is persisted for restart", store.PendingPrivacyWork && loaded.NotesCleanupPending);
    }
    using (var store = new LocalStore(pendingRoot))
    {
        var loaded = store.Load();
        Check("restart resumes pending cleanup without losing notes", !store.PendingPrivacyWork && !loaded.NotesCleanupPending && loaded.Accounts[0].Notes == pendingNote);
    }
    using (var store = new LocalStore(Path.Combine(root,"LoginInfoMigration")))
    {
        const string url="https://2fa.example.invalid/", key="JBSWY3DPEHPK3PXP";
        var legacy=Make(false); legacy.Notes=""; legacy.ProtectedNotes=SecretVault.ProtectNotes(url+"\r\n"+key);
        legacy.RechargeDate=new DateOnly(2026,9,15); legacy.SubscriptionDate=new DateOnly(2026,10,15);
        legacy.ProtectedPassword=SecretVault.Protect("Synthetic-password");
        using(var command=Connection(store).CreateCommand())
        {
            command.CommandText="INSERT INTO app_state(id,version,json) VALUES(1,2,$json)";
            command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(new AppData{Version=2,Accounts=[legacy]},LocalStore.Json)); command.ExecuteNonQuery();
        }
        Execute(store,"PRAGMA query_only=ON");
        using var service=new AccountService(store,missing);
        var a=service.Data.Accounts[0];
        Check("encrypted legacy two-line notes migrate to separate 2FA fields",SecretVault.RevealTwoFactorUrl(a.ProtectedTwoFactorUrl)==url&&SecretVault.RevealTwoFactorKey(a.ProtectedTwoFactorKey)==key&&a.Notes==url+"\r\n"+key);
        Check("failed login-info migration remains retryable",service.HasUnsavedChanges&&store.PendingDataUpgrade&&!service.Save());
        Execute(store,"PRAGMA query_only=OFF");
        Check("login-info migration persists after storage recovers",service.Save()&&!store.PendingDataUpgrade&&store.Load().Version==3);
        using(var command=Connection(store).CreateCommand())
        {
            command.CommandText="SELECT json FROM app_state WHERE id=1";var raw=(string)command.ExecuteScalar()!;
            Check("2FA URL and key are encrypted on disk",!raw.Contains(url)&&!raw.Contains(key));
        }
        Check("migration preserves identity, password, dates and absent legacy times",a.Id==legacy.Id&&a.ProtectedPassword==legacy.ProtectedPassword&&a.RechargeDate==legacy.RechargeDate&&a.SubscriptionDate==legacy.SubscriptionDate&&a.RechargeTime is null&&a.SubscriptionTime is null);
        var edit=LocalStore.CopyAccount(a);edit.RechargeTime=new TimeOnly(8,15);edit.SubscriptionTime=new TimeOnly(22,45);edit.ProtectedTwoFactorUrl=null;edit.ProtectedTwoFactorKey=null;
        service.UpdateAccount(edit);var reloaded=store.Load().Accounts[0];
        Check("cleared 2FA values do not return from legacy notes on restart",reloaded.LoginInfoInitialized&&reloaded.ProtectedTwoFactorUrl is null&&reloaded.ProtectedTwoFactorKey is null);
        Check("edited subscription times survive save and reload",reloaded.RechargeTime==new TimeOnly(8,15)&&reloaded.SubscriptionTime==new TimeOnly(22,45));
        edit=LocalStore.CopyAccount(service.Data.Accounts[0]);edit.ProtectedTwoFactorUrl=SecretVault.ProtectTwoFactorUrl(url);edit.ProtectedTwoFactorKey=SecretVault.ProtectTwoFactorKey(key);service.UpdateAccount(edit);
        var exportPath=Path.Combine(root,"login-metadata.json");store.ExportMetadata(service.Data,exportPath);var exported=File.ReadAllText(exportPath);
        Check("metadata export excludes 2FA plaintext and ciphertext",!exported.Contains(url)&&!exported.Contains(key)&&!exported.Contains(edit.ProtectedTwoFactorKey!)&&!exported.Contains(edit.ProtectedTwoFactorUrl!));
        service.Data.Accounts[0].History.Add(new("synthetic-window","codex",DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow,"scheduled"));service.Save();
        var fullPath=Path.Combine(root,"complete-accounts.json");store.ExportAll(service.Data,fullPath);
        var complete=JsonSerializer.Deserialize<AccountExport>(File.ReadAllText(fullPath),LocalStore.Json)!;
        Check("complete export includes plaintext password, 2FA and notes",complete.LoginInfo[a.Id].Password=="Synthetic-password"&&complete.LoginInfo[a.Id].TwoFactorUrl==url&&complete.LoginInfo[a.Id].TwoFactorKey==key&&complete.Data.Accounts[0].Notes==a.Notes);
        Check("complete export excludes Windows-bound ciphertext and auth sessions",complete.Data.Accounts[0].ProtectedPassword is null&&complete.Data.Accounts[0].ProtectedNotes is null&&complete.Data.Accounts[0].ProtectedTwoFactorKey is null&&complete.Data.Accounts[0].AuthState=="unlinked");
        var completeImport=store.ImportMetadata(fullPath)[0];
        Check("complete import restores secrets with fresh encryption and requires authorization",completeImport.Id!=a.Id&&completeImport.AuthState=="unlinked"&&SecretVault.Reveal(completeImport.ProtectedPassword)=="Synthetic-password"&&SecretVault.RevealTwoFactorUrl(completeImport.ProtectedTwoFactorUrl)==url&&SecretVault.RevealTwoFactorKey(completeImport.ProtectedTwoFactorKey)==key);
        using(var destination=new LocalStore(Path.Combine(root,"CompleteImport")))
        {
            destination.Save(new(){Accounts=[completeImport]});var imported=destination.Load().Accounts[0];
            Check("complete import retains dates, times, notes and history after restart",imported.RechargeDate==a.RechargeDate&&imported.RechargeTime==new TimeOnly(8,15)&&imported.SubscriptionTime==new TimeOnly(22,45)&&imported.Notes==a.Notes&&SecretVault.RevealTwoFactorKey(imported.ProtectedTwoFactorKey)==key&&imported.History.SequenceEqual(service.Data.Accounts[0].History));
        }
        complete.LoginInfo.Clear();var badFullPath=Path.Combine(root,"incomplete-accounts.json");File.WriteAllText(badFullPath,JsonSerializer.Serialize(complete));var invalidFull=false;
        try{store.ImportMetadata(badFullPath);}catch(InvalidDataException){invalidFull=true;}
        Check("incomplete full backups are rejected without changing accounts",invalidFull&&service.Data.Accounts.Count==1);
        var unreadable=LocalStore.CopyAccount(service.Data.Accounts[0]);unreadable.ProtectedTwoFactorKey="invalid-base64";service.UpdateAccount(unreadable);
        Check("unreadable 2FA ciphertext is retained on unrelated edits",store.Load().Accounts[0].ProtectedTwoFactorKey=="invalid-base64");
        var previousExport=File.ReadAllText(fullPath);var failedExport=false;
        try{store.ExportAll(service.Data,fullPath);}catch(FormatException){failedExport=true;}
        Check("failed complete export preserves an existing backup file",failedExport&&File.ReadAllText(fullPath)==previousExport);
        var unrelated=Make(false);unrelated.Notes="This is an ordinary note, not a URL and secret.";LocalStore.MigrateLoginInfo(unrelated);
        Check("ordinary notes are never treated as 2FA secrets",unrelated.ProtectedTwoFactorUrl is null&&unrelated.ProtectedTwoFactorKey is null);
    }
    File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { success = true, checks }));
    Console.WriteLine($"{checks.Count} storage, privacy, concurrency and deletion checks passed.");
}
catch
{
    File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { success = false, checks })); throw;
}
