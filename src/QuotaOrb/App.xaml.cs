using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using QuotaOrb.Services;
using Forms = System.Windows.Forms;

namespace QuotaOrb;

public partial class App : Application
{
    public LocalStore Store { get; private set; } = null!;
    public AccountService Service { get; private set; } = null!;
    public DesktopSwitcher? DesktopSwitch { get; private set; }
    public string? DesktopSwitchError { get; private set; }
    public bool Exiting { get; private set; }
    private MainWindow? dashboard;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    internal System.Drawing.Icon? TrayIcon => trayIcon;
    private Mutex? mutex;
    private EventWaitHandle? openSignal;
    private EventWaitHandle? quitSignal;
    private RegisteredWaitHandle? openRegistration;
    private RegisteredWaitHandle? quitRegistration;
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTimeOffset lastPulse = DateTimeOffset.UtcNow;
    private DateTimeOffset lastPoll = DateTimeOffset.MinValue;
    private readonly HashSet<string> reminded = [];
    private bool polling;
    private bool smoke;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        smoke = e.Args.Contains("--smoke-test");
        var rootIndex = Array.IndexOf(e.Args,"--data-root");
        var root = rootIndex >= 0 && rootIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[rootIndex+1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaOrb");
        var normalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QuotaOrb");
        if (e.Args.Contains("--quit")) { Signal("Local\\QuotaOrb.Quit"); Shutdown(); return; }
        // Test mode must never overwrite real or existing application data.
        if (smoke && (rootIndex < 0 || string.Equals(Path.TrimEndingDirectorySeparator(root),normalRoot,StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(root,"accounts.db")))) { Shutdown(2); return; }
        if (!smoke)
        {
            mutex = new Mutex(true,"Local\\QuotaOrb.Desktop",out var first);
            if (!first)
            {
                if (!Signal("Local\\QuotaOrb.Open")) MessageBox.Show("额度管理已在运行，请双击右下角托盘图标打开。","额度管理");
                Shutdown(); return;
            }
        }
        try
        {
            Store = new LocalStore(root);
            Service = new AccountService(Store,new CodexInstallation());
            if (!smoke)
            {
                try
                {
                    DesktopSwitch = new DesktopSwitcher(root, new WindowsDesktopRuntime(new CodexInstallation()), new DesktopLoginProbe(new CodexInstallation()));
                    DesktopSwitch.Changed += () => Dispatcher.InvokeAsync(UpdateViews);
                }
                catch { DesktopSwitchError = "桌面登录缓存无法打开，查询额度仍可使用。请检查本地数据目录权限。"; }
            }
            if (smoke) SmokeChecks.Seed(Service);
            Service.Changed += UpdateViews;
            Service.Notice += ShowNotice;
            dashboard = new MainWindow(this); MainWindow = dashboard;
            // Create its hidden handle too, so the shortcut works for --background startup.
            new System.Windows.Interop.WindowInteropHelper(dashboard).EnsureHandle();
            if (!e.Args.Contains("--background")) dashboard.Show();
            UpdateViews();
            CreateTray();
            if (smoke) { Dispatcher.InvokeAsync(async () => await SmokeChecks.RunAsync(this,dashboard,root)); return; }
            openSignal = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QuotaOrb.Open");
            quitSignal = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QuotaOrb.Quit");
            openRegistration = ThreadPool.RegisterWaitForSingleObject(openSignal,(_,_)=>Dispatcher.InvokeAsync(ShowDashboard),null,Timeout.Infinite,false);
            quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitSignal,(_,_)=>Dispatcher.InvokeAsync(Quit),null,Timeout.Infinite,false);
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
            pulse.Tick += Pulse; pulse.Start();
            _ = PollAsync();
        }
        catch (Exception error)
        {
            if (smoke) File.WriteAllText(Path.Combine(root,"result.json"),System.Text.Json.JsonSerializer.Serialize(new{success=false,error=error.ToString()}));
            else MessageBox.Show("额度管理启动失败。原有数据文件已保留，请检查本地目录权限及安装包完整性。","额度管理",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ToggleDashboard()
    {
        if (dashboard is null) return;
        if (dashboard.IsVisible && dashboard.IsActive) { dashboard.Hide(); }
        else ShowDashboard();
    }
    public void ShowDashboard()
    {
        if (Exiting || dashboard is null) return;
        dashboard.Show(); dashboard.WindowState = WindowState.Normal; dashboard.Activate(); UpdateViews();
        if (!smoke && AutomaticPollDue(DateTimeOffset.UtcNow)) _ = PollAsync();
    }
    private static bool Signal(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    public void UpdateViews()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(UpdateViews); return; }
        if (Exiting) return;
        if (dashboard?.IsVisible == true) dashboard.Render();
        var count = Service.Data.Accounts.Count(a=>QuotaPolicy.Evaluate(a,Service.Data.Settings.LimitId,DateTimeOffset.UtcNow).CanRecommend);
        if (tray is not null) tray.Text = $"额度管理 · {count} 个账号额度可用";
    }

    private async void Pulse(object? sender,EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        if (now-lastPulse>TimeSpan.FromSeconds(75) || now<lastPulse) { Service.Invalidate(); lastPoll=DateTimeOffset.MinValue; }
        lastPulse=now;
        UpdateViews();
        CheckDateNotices();
        if (!Service.Data.Settings.AutoRefresh || Service.Busy || polling) return;
        // A passed quota reset updates the display; scheduled polling still follows the 15/30-minute cadence.
        if (AutomaticPollDue(now)) await PollAsync();
    }
    private bool AutomaticPollDue(DateTimeOffset now) => RefreshPolicy.IsDue(lastPoll, now,
        dashboard?.IsVisible == true && dashboard.WindowState != WindowState.Minimized);
    private async Task PollAsync()
    {
        if (polling || Service.Busy || !Service.ComponentAvailable || !Service.Data.Settings.AutoRefresh || dashboard?.OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)==true) return;
        polling=true; lastPoll=DateTimeOffset.UtcNow;
        try { await Service.RefreshAllAsync(false); }
        catch { ShowNotice("查询暂未完成","请稍后手动核验。账号信息仍保存在本机。"); }
        finally { polling=false; UpdateViews(); }
    }
    private void Resume()
    {
        if (Exiting) return; Service.Invalidate(); lastPoll=DateTimeOffset.MinValue; _=PollAsync();
    }
    private void PowerChanged(object sender,PowerModeChangedEventArgs e) { if (e.Mode==PowerModes.Resume) Dispatcher.InvokeAsync(Resume); }
    private void NetworkChanged(object? sender,NetworkAvailabilityEventArgs e) { if(e.IsAvailable) Dispatcher.InvokeAsync(Resume); else Dispatcher.InvokeAsync(()=>{Service.Invalidate(); UpdateViews();}); }
    private void SessionChanged(object sender,SessionSwitchEventArgs e) { if(e.Reason==SessionSwitchReason.SessionLock) Dispatcher.InvokeAsync(SafeClipboard.ClearOwned); }
    private void CheckDateNotices()
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var a in Service.Data.Accounts.Where(a=>a.Enabled))
        {
            if(a.SubscriptionDate is { } date && date.ToDateTime(a.SubscriptionTime ?? new TimeOnly(12,0)) is var expiration && expiration>DateTime.Now && expiration<=DateTime.Now.AddDays(3))
            { var key=$"subscription:{a.Id}:{expiration:O}"; if(reminded.Add(key)) ShowNotice("订阅即将到期",$"{a.Name}：订阅到期时间为 {expiration:MM/dd HH:mm}。"); }
            if(a.LastError is not null || a.Snapshot is not { } s || now-s.FetchedAt>QuotaPolicy.Freshness) continue;
            foreach(var card in s.ResetCredits??[])
                if(card.Status=="available" && card.ExpiresAt is { } expiry && expiry>now && expiry<now.AddDays(2) && reminded.Add($"card:{a.Id}:{card.Id}:{expiry}"))
                    ShowNotice("重置卡即将到期",$"{a.Name} 的一张重置卡将于 {expiry.ToLocalTime():MM/dd HH:mm} 到期。");
        }
    }
    private void CreateTray()
    {
        trayIcon = AppIcons.LoadTrayIcon();
        tray = new Forms.NotifyIcon { Icon=trayIcon,Text="额度管理",Visible=!smoke };
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("打开账号列表",null,(_,_)=>Dispatcher.Invoke(ShowDashboard));
        menu.Items.Add("全部核验",null,(_,_)=>Dispatcher.InvokeAsync(async()=>
        { try { await Service.RefreshAllAsync(true); } catch { ShowNotice("核验暂未完成","请稍后重试。账号信息仍保存在本机。"); } }));
        menu.Items.Add("重试保存",null,(_,_)=>Dispatcher.Invoke(()=>{if(!Service.Busy)Service.Save();}));
        menu.Items.Add("设置",null,(_,_)=>Dispatcher.Invoke(()=>{ShowDashboard();new SettingsWindow(this){Owner=dashboard}.ShowDialog();}));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出额度管理",null,(_,_)=>Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip=menu; tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowDashboard);
    }
    private void ShowNotice(string title,string message)
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(()=>ShowNotice(title,message));return;}
        if(!Service.Data.Settings.Notifications || tray is null || Exiting) return;
        try { tray.ShowBalloonTip(5000,title,message,Forms.ToolTipIcon.Info); } catch (System.ComponentModel.Win32Exception) { }
    }
    public void Quit() { Exiting=true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting=true; pulse.Stop();
        if(!smoke) {SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.SessionSwitch-=SessionChanged;NetworkChange.NetworkAvailabilityChanged-=NetworkChanged;}
        openRegistration?.Unregister(null); quitRegistration?.Unregister(null);
        openSignal?.Dispose(); quitSignal?.Dispose();
        SafeClipboard.ClearOwned();
        if(tray is not null){tray.Visible=false;tray.Dispose();}
        trayIcon?.Dispose();
        if(Service is not null){Service.Dispose();try{Service.Data.LastRunningUtc=DateTimeOffset.UtcNow;Store.Save(Service.Data);}catch{}}
        Store?.Dispose(); mutex?.Dispose(); base.OnExit(e);
    }
}
