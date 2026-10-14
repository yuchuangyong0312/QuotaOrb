using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuotaOrb.Services;

namespace QuotaOrb;

internal static class SmokeChecks
{
    public static void Seed(AccountService service)
    {
        var now=DateTimeOffset.UtcNow;
        service.Data.Accounts.Clear();
        AccountRecord Make(string name,string email,double s,double w,bool? allowed=true)=>new()
        {
            Name=name,Email=email,Plan="plus",AuthState="ready",ProtectedPassword=SecretVault.Protect("test-only-no-real-secret"),
            ProtectedTwoFactorUrl=SecretVault.ProtectTwoFactorUrl("https://2fa.example.invalid/"),ProtectedTwoFactorKey=SecretVault.ProtectTwoFactorKey("JBSWY3DPEHPK3PXP"),LoginInfoInitialized=true,
            RechargeDate=new DateOnly(2026,9,1),SubscriptionDate=new DateOnly(2026,10,1),Notes="Synthetic-private-note；独立运行测试数据，未连接真实账号。",LastSuccessUtc=now,
            Snapshot=new(now,"test-"+email,allowed,new(){["codex"]=new("codex",null,"plus",new(s,300,now.AddHours(2)),new(w,10080,now.AddDays(2)),null,null,null)},2,[new("test-card","available","codexRateLimits",now.AddDays(1),"重置卡")])
        };
        service.Data.Accounts.Add(Make("备用 B","backup-b@example.com",8,24));
        service.Data.Accounts.Add(Make("开发主号","main-a@example.com",100,36));
        service.Data.Accounts.Add(Make("测试账号","test-c@example.com",45,100));
        var stale=Make("休假三天的账号","holiday@example.com",70,80);stale.Snapshot=stale.Snapshot! with{FetchedAt=now.AddDays(-3)};stale.LastSuccessUtc=now.AddDays(-3);stale.Gaps.Add(new(now.AddDays(-3),now));service.Data.Accounts.Add(stale);
        service.Data.Accounts.Add(new(){Name="尚未授权",Email="new@example.com"});
        service.Save();
    }
    public static async Task RunAsync(App app,MainWindow window,string root)
    {
        var checks=new List<string>();
        void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            await Task.Delay(300);
            window.Render();window.UpdateLayout();
            Check("native five-account list",window.AccountsGrid.Items.Count==5);
            Check("UI smoke never opens the real desktop login cache",app.DesktopSwitch is null&&!Directory.Exists(Path.Combine(root,"desktop-logins")));
            Check("desktop switch requires a saved login and live adapter",!window.SaveDesktopButton.IsEnabled&&!window.SwitchDesktopButton.IsEnabled&&window.DesktopRecoveryActions.Visibility==Visibility.Collapsed);
            Check("only dashboard exists, no floating window",app.Windows.Count==1&&window.ShowInTaskbar);
            Check("window icon is embedded",window.Icon is not null);
            var arguments = Environment.GetCommandLineArgs();
            var expectedIconIndex = Array.IndexOf(arguments,"--expected-icon");
            using (var expected = expectedIconIndex>=0 && expectedIconIndex+2<arguments.Length
                ? AppIcons.LoadFromFile(arguments[expectedIconIndex+1],int.Parse(arguments[expectedIconIndex+2]))!
                : AppIcons.LoadTrayIcon())
            using (var expectedBitmap = expected.ToBitmap())
            using (var trayBitmap = app.TrayIcon!.ToBitmap())
                Check("tray matches the desktop or packaged icon",Enumerable.Range(0,16).All(x=>Enumerable.Range(0,16).All(y=>expectedBitmap.GetPixel(x,y)==trayBitmap.GetPixel(x,y))));
            using (var embedded = AppIcons.LoadEmbedded())
            using (var embeddedBitmap = embedded.ToBitmap())
            using (var executableIcon = AppIcons.LoadFromFile(Environment.ProcessPath!,0)!)
            using (var executableBitmap = executableIcon.ToBitmap())
                Check("EXE and desktop default use the same icon",Enumerable.Range(0,16).All(x=>Enumerable.Range(0,16).All(y=>embeddedBitmap.GetPixel(x,y)==executableBitmap.GetPixel(x,y))));
            Check("licenses are contained in the application",typeof(App).Assembly.GetManifestResourceNames().Count(n=>n.StartsWith("QuotaOrb.Licenses."))==6);
            var settings = new SettingsWindow(app) { Owner = window };
            var settingsPanel = (StackPanel)settings.Content;
            Check("settings have no floating-ball control",!settingsPanel.Children.OfType<CheckBox>().Any(c=>c.Content?.ToString()?.Contains("悬浮球")==true));
            settings.Close();
            Check("old holiday data excluded",!QuotaPolicy.Evaluate(app.Service.Data.Accounts[3],"codex",DateTimeOffset.UtcNow).CanRecommend);
            Check("available account leads the list",window.AccountsGrid.Items.OfType<AccountRow>().First().Name=="备用 B" && window.AccountsGrid.Items.OfType<AccountRow>().Count(a=>a.Decision.CanRecommend)==1);
            var plain="test-only-no-real-secret";
            Check("DPAPI round trip",SecretVault.Reveal(app.Service.Data.Accounts[0].ProtectedPassword)==plain);
            using(var database=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"accounts.db"),Mode=Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly}.ToString()))
            {database.Open();using var command=database.CreateCommand();command.CommandText="SELECT json FROM app_state WHERE id=1";var raw=(string)command.ExecuteScalar()!;Check("password encrypted at rest",!raw.Contains(plain));Check("notes encrypted at rest",!raw.Contains("Synthetic-private-note"));}
            var loaded=app.Store.Load();Check("SQLite persists all records",loaded.Accounts.Count==5);
            Check("saved notes decrypt without data loss",loaded.Accounts[0].Notes==app.Service.Data.Accounts[0].Notes);
            var connection=(Microsoft.Data.Sqlite.SqliteConnection)typeof(LocalStore).GetField("connection",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(app.Store)!;
            using(var command=connection.CreateCommand())
            {
                command.CommandText="PRAGMA query_only=ON";command.ExecuteNonQuery();
                try { Check("write failure displays a retry banner",!app.Service.Save()&&window.StoragePanel.Visibility==Visibility.Visible&&window.RetrySaveButton.IsEnabled); }
                finally { command.CommandText="PRAGMA query_only=OFF";command.ExecuteNonQuery(); }
            }
            window.RetrySaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("retry button clears the failure banner after saving",window.StoragePanel.Visibility==Visibility.Collapsed&&!app.Service.HasUnsavedChanges);
            app.Store.ExportMetadata(app.Service.Data,Path.Combine(root,"backup-check.json"));
            var backup=File.ReadAllText(Path.Combine(root,"backup-check.json"));
            Check("metadata backup excludes password",!backup.Contains(plain)&&!backup.Contains(app.Service.Data.Accounts[0].ProtectedPassword!));
            Check("metadata backup excludes notes by default",!backup.Contains("Synthetic-private-note")&&!backup.Contains(app.Service.Data.Accounts[0].ProtectedNotes!));
            var imported=app.Store.ImportMetadata(Path.Combine(root,"backup-check.json"));
            Check("import requires fresh authorization",imported.All(a=>a.AuthState=="unlinked"&&a.ProtectedPassword is null)&&imported.All(a=>loaded.Accounts.All(b=>a.Id!=b.Id)));
            loaded.Accounts[0].Snapshot=loaded.Accounts[0].Snapshot! with{Buckets=null!};
            var malformedPath=Path.Combine(root,"malformed-backup.json");File.WriteAllText(malformedPath,JsonSerializer.Serialize(loaded));
            var rejected=false;try{app.Store.ImportMetadata(malformedPath);}catch(InvalidDataException){rejected=true;}
            Check("malformed import rejected before modifying data",rejected&&app.Service.Data.Accounts.Count==5);
            var id=app.Service.Data.Accounts[0].Id;
            Check("profile path stable independent of installation",app.Store.ProfilePath(id)==Path.Combine(root,"profiles",id));
            var component=app.Service.ComponentPath;
            Check("installed Codex is discovered",component is not null && File.Exists(component));
            Check("application has no bundled Codex runtime",!Directory.Exists(Path.Combine(AppContext.BaseDirectory,"runtime")));
            {
                await using var client=new CodexClient(component!,app.Store.ProfilePath(Guid.NewGuid().ToString("N")));
                await client.InitializeAsync(CancellationToken.None);
                var identity=await client.RequestAsync("account/read",new{refreshToken=false},CancellationToken.None);
                Check("C# client uses empty isolated profile",QuotaParser.Get(identity,"account") is null or {ValueKind:JsonValueKind.Null});
                // Layout-only checks may run while the user owns the real callback port.
                if (!arguments.Contains("--skip-auth-handoff"))
                {
                    var login=await client.RequestAsync("account/login/start",new{type="chatgpt",useHostedLoginSuccessPage=true},CancellationToken.None);
                    Check("C# client receives official OAuth handoff",LoginFlow.OfficialUrl(QuotaParser.Text(login,"authUrl")).Scheme=="https");
                    await client.RequestAsync("account/login/cancel",new{loginId=QuotaParser.Text(login,"loginId")},CancellationToken.None);
                    Check("C# login cancel completes",true);
                }
            }
            var firstCard=(FrameworkElement)window.AccountsGrid.ItemContainerGenerator.ContainerFromIndex(0);
            var secondCard=(FrameworkElement)window.AccountsGrid.ItemContainerGenerator.ContainerFromIndex(1);
            Check("wide view shows two cards per row",Math.Abs(firstCard.TranslatePoint(new Point(),window.AccountsGrid).Y-secondCard.TranslatePoint(new Point(),window.AccountsGrid).Y)<1);
            Capture(window,Path.Combine(root,"window.png"));
            Check("login password and 2FA fields display plaintext",window.PasswordDisplay.Text==plain&&window.TwoFactorUrlDisplay.Text=="https://2fa.example.invalid/"&&window.TwoFactorKeyDisplay.Text=="JBSWY3DPEHPK3PXP");
            Check("each copy button targets its own complete login value",ReferenceEquals(window.CopyEmailButton.Tag,window.EmailDisplay)&&ReferenceEquals(window.CopyPasswordButton.Tag,window.PasswordDisplay)&&ReferenceEquals(window.CopyTwoFactorUrlButton.Tag,window.TwoFactorUrlDisplay)&&ReferenceEquals(window.CopyTwoFactorKeyButton.Tag,window.TwoFactorKeyDisplay)&&(string)window.TwoFactorKeyDisplay.Tag=="JBSWY3DPEHPK3PXP");
            Check("legacy dates default to noon and explicit time is retained",MainWindow.DateText(new DateOnly(2026,9,15),null)=="2026/09/15 12:00"&&MainWindow.DateText(new DateOnly(2026,10,15),new TimeOnly(18,45))=="2026/10/15 18:45"&&MainWindow.DateText(null,null)=="无");
            Check("editor accepts valid time and rejects impossible time",AccountEditor.TryTime("18:45",out var parsed)&&parsed==new TimeOnly(18,45)&&AccountEditor.TryTime("",out parsed)&&parsed==new TimeOnly(12,0)&&!AccountEditor.TryTime("25:61",out _));
            var selected = window.AccountsGrid.Items.OfType<AccountRow>().First(r=>r.Name=="尚未授权");
            window.AccountsGrid.SelectedItem=selected;window.UpdateLayout();
            Check("card selection updates account details",window.DetailName.Text==selected.Name);
            Check("unknown quota stays unknown in rings",selected.ShortRemaining is null&&selected.WeekRemaining is null);
            Check("missing login values clear previous secrets and show none",window.PasswordDisplay.Text=="无"&&window.TwoFactorUrlDisplay.Text=="无"&&window.TwoFactorKeyDisplay.Text=="无"&&!window.CopyPasswordButton.IsEnabled&&window.PasswordDisplay.Tag is null);
            var exhausted=window.AccountsGrid.Items.OfType<AccountRow>().First(r=>r.Name=="开发主号");
            Check("exhausted and unknown quotas remain distinct",exhausted.ShortRemaining==0&&selected.ShortRemaining is null);
            window.Width=980;window.Height=640;
            await app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();Capture(window,Path.Combine(root,"window-narrow.png"));
            Check("narrow view fits account cards",window.AccountsGrid.Items.Cast<AccountRow>().All(row=>window.AccountsGrid.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement item || item.ActualWidth<=window.AccountsGrid.ActualWidth));
            var accountScroll=MainWindow.FindVisualChild<ScrollViewer>(window.AccountsGrid)!;
            accountScroll.ScrollToVerticalOffset(220);window.UpdateLayout();
            var previousOffset=accountScroll.VerticalOffset;
            window.Render();window.UpdateLayout();
            await app.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            Check("periodic rendering preserves account scroll position",previousOffset>0&&Math.Abs(accountScroll.VerticalOffset-previousOffset)<1);
            var editor=new AccountEditor(new AccountRecord(),true){Owner=window};editor.Show();editor.UpdateLayout();Capture(editor,Path.Combine(root,"editor.png"));editor.Close();
            var appearanceSettings=new SettingsWindow(app){Owner=window};appearanceSettings.Show();appearanceSettings.UpdateLayout();Capture(appearanceSettings,Path.Combine(root,"settings.png"));appearanceSettings.Close();
            await CheckLoginWindowAsync(window, root, Check);
            var accounts=app.Service.Data.Accounts.ToList();
            try
            {
                app.Service.Data.Accounts.Clear();window.Render();window.UpdateLayout();
                Check("empty account view disables detail actions",window.EmptyState.Visibility==Visibility.Visible&&!window.DetailPanel.IsEnabled);
                Capture(window,Path.Combine(root,"window-empty.png"));
            }
            finally {app.Service.Data.Accounts.AddRange(accounts);window.Render();}
            window.Close();Check("closing panel retains application",!window.IsVisible&&!app.Exiting);
            app.ShowDashboard();Check("tray action opens panel",window.IsVisible);
            window.WindowState=WindowState.Minimized;app.ShowDashboard();Check("opening dashboard restores minimized window",window.WindowState==WindowState.Normal);
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{success=true,checks}));
        }
        catch(Exception e){File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{success=false,error=e.ToString(),checks}));}
        finally{app.Quit();}
    }
    private static async Task CheckLoginWindowAsync(MainWindow window, string root, Action<string,bool> check)
    {
        var token = CancellationToken.None;
        var finished = new TaskCompletionSource<LoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new List<LoginMethod>();
        var fixture = new AccountRecord { Name = "授权窗口测试", Email = "login@example.invalid" };
        var dialog = new LoginWindow(fixture, async (method, present, cancellation) =>
        {
            methods.Add(method); token = cancellation;
            present(new(method, new Uri("https://auth.openai.com/codex/device"), method == LoginMethod.DeviceCode ? "TEST-0000" : null, DateTimeOffset.UtcNow.AddMinutes(15)));
            return await finished.Task.WaitAsync(cancellation);
        }, _ => { });
        window.ShowLoginWindow(dialog);
        await Task.Delay(100); dialog.UpdateLayout();
        check("authorization window has a native minimize control",dialog.ResizeMode==ResizeMode.CanMinimize);
        check("modeless authorization leaves native dashboard and copy buttons enabled",NativeWindowEnabled(new System.Windows.Interop.WindowInteropHelper(window).Handle)&&window.CopyEmailButton.IsEnabled);
        dialog.WindowState=WindowState.Minimized;
        check("minimize keeps authorization waiting",dialog.WindowState==WindowState.Minimized&&!token.IsCancellationRequested&&dialog.IsAuthorizing);
        dialog.WindowState=WindowState.Normal; dialog.Close();
        check("titlebar close minimizes without cancelling authorization",dialog.WindowState==WindowState.Minimized&&!token.IsCancellationRequested&&dialog.IsAuthorizing);
        window.LoginButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check("dashboard reopens the same pending authorization",dialog.WindowState==WindowState.Normal&&methods.Count==1);
        dialog.UpdateLayout();Capture(dialog,Path.Combine(root,"login-waiting.png"));
        dialog.DeviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);dialog.UpdateLayout();
        check("device-code fallback cancels prior attempt before starting the next",methods.SequenceEqual(new[]{LoginMethod.Browser,LoginMethod.DeviceCode})&&dialog.Code.Text=="TEST-0000"&&dialog.IsAuthorizing);
        Capture(dialog,Path.Combine(root,"login-device-code.png"));
        finished.SetResult(new(false,false,"本次官方授权未完成。请关闭旧登录网页后重试，或改用设备码授权。"));
        await Task.Delay(100);dialog.UpdateLayout();
        check("failed authorization retains a retry action and clears transient code",dialog.IsVisible&&!dialog.IsAuthorizing&&dialog.DeviceButton.Content?.ToString()=="重新授权"&&dialog.Code.Text=="");
        Capture(dialog,Path.Combine(root,"login-retry.png"));
        finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.DeviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        check("retry starts a fresh attempt",methods.Count==3&&dialog.IsAuthorizing);
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        check("explicit cancel ends authorization and closes the window",token.IsCancellationRequested&&!dialog.IsAuthorizing&&!dialog.IsVisible);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="IsWindowEnabled")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool NativeWindowEnabled(IntPtr window);
    private static void Capture(Window window,string path)
    {
        var visual=(FrameworkElement)window.Content;
        var width=(int)Math.Ceiling(visual.ActualWidth+visual.Margin.Left+visual.Margin.Right);
        var height=(int)Math.Ceiling(visual.ActualHeight+visual.Margin.Top+visual.Margin.Bottom);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
