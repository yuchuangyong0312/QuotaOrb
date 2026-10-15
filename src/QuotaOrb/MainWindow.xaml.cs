using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Reflection;
using QuotaOrb.Services;

namespace QuotaOrb;

public partial class MainWindow : Window
{
    private readonly App app;
    private AccountService Service => app.Service;
    private bool rendering;
    private HwndSource? hotkeySource;
    private const int DashboardHotkeyId = 0x514F;
    private string? selectedId;
    private LoginWindow? loginWindow;
    private AccountRecord? Selected => Service.Data.Accounts.FirstOrDefault(a => a.Id == selectedId);
    public MainWindow(App app)
    {
        this.app = app; InitializeComponent(); UiTheme.Apply(this);
        if (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Contains("-preview",StringComparison.Ordinal) == true)
            Title += " · 账号切换测试版";
        Closing += (_, e) => { if (!app.Exiting) { e.Cancel = true; Hide(); } };
        SourceInitialized += (_, _) =>
        {
            hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hotkeySource?.AddHook(HandleHotkey);
            RegisterHotKey(new WindowInteropHelper(this).Handle,DashboardHotkeyId,0x0002|0x0001|0x4000,0x51);
        };
        Closed += (_, _) =>
        {
            if (hotkeySource is not null) { UnregisterHotKey(hotkeySource.Handle,DashboardHotkeyId); hotkeySource.RemoveHook(HandleHotkey); }
        };
        Render();
    }

    public void Render()
    {
        if (!IsInitialized) return;
        rendering = true;
        var scroller = FindVisualChild<ScrollViewer>(AccountsGrid);
        var scrollOffset = scroller?.VerticalOffset ?? 0;
        var now = DateTimeOffset.UtcNow;
        var pool = Service.Data.Settings.LimitId;
        var rows = Service.Data.Accounts.Select(a => new AccountRow(a, pool, now, Service.BusyAccountId == a.Id))
            .OrderBy(a => a.Decision.State == Availability.Available ? 0 : a.Decision.State == Availability.Exhausted ? 1 : 2)
            .ThenBy(a => a.Name).ToList();
        AccountsGrid.ItemsSource = rows;
        if (selectedId is null || rows.All(r => r.Record.Id != selectedId)) selectedId = rows.FirstOrDefault()?.Record.Id;
        AccountsGrid.SelectedItem = rows.FirstOrDefault(r => r.Record.Id == selectedId);
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountCount.Text = $"{rows.Count} 个账号";
        EmptyLabel.Text = Service.ComponentAvailable ? "还没有账号\n\n点击右上角“添加账号”，再完成官方登录授权。" : CodexInstallation.MissingMessage;
        RefreshAllButton.IsEnabled = !Service.Busy;
        StoragePanel.Visibility = Service.StorageError is null ? Visibility.Collapsed : Visibility.Visible;
        StorageMessage.Text = Service.StorageError ?? "";
        RetrySaveButton.IsEnabled = !Service.Busy;
        rendering = false;
        RenderDetails();
        if (scroller is not null && scrollOffset > 0)
        {
            AccountsGrid.UpdateLayout();
            scroller.ScrollToVerticalOffset(scrollOffset);
        }
    }

    internal static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void ResetCredits_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccountRow row, ContextMenu: { } menu } button)
        {
            menu.DataContext = row;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void RenderDetails()
    {
        var a = Selected;
        var desktop = app.DesktopSwitch;
        var pending = desktop?.HasPending == true;
        DetailPanel.IsEnabled = a is not null || pending;
        var authorized = a is not null && desktop?.HasAuthorized(a) == true;
        DesktopSwitchMessage.Text = app.DesktopSwitchError ?? desktop?.GetMessage(a) ?? "界面测试模式不会读取或改动真实 Codex 登录。";
        AuthorizeDesktopButton.IsEnabled = loginWindow is not null || (a is not null && desktop is not null && !pending && !Service.Busy);
        SwitchDesktopButton.IsEnabled = authorized && !pending && !Service.Busy && a!.Enabled;
        DesktopRecoveryActions.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        RestoreDesktopButton.IsEnabled = KeepDesktopButton.IsEnabled = !Service.Busy;
        SetLoginField(EmailDisplay, CopyEmailButton, a?.Email);
        SetSecretField(PasswordDisplay, CopyPasswordButton, () => SecretVault.Reveal(a?.ProtectedPassword));
        SetSecretField(TwoFactorUrlDisplay, CopyTwoFactorUrlButton, () => SecretVault.RevealTwoFactorUrl(a?.ProtectedTwoFactorUrl));
        SetSecretField(TwoFactorKeyDisplay, CopyTwoFactorKeyButton, () => SecretVault.RevealTwoFactorKey(a?.ProtectedTwoFactorKey));
        if (a is null)
        {
            DetailName.Text = "选择一个账号";
            foreach (var t in new[] { DetailError, RechargeDetail }) t.Text = "";
            DetailError.Visibility = Visibility.Collapsed;
            return;
        }
        DetailName.Text = a.Name;
        DetailError.Text = Service.ComponentAvailable ? a.LastError ?? "" : CodexInstallation.MissingMessage;
        DetailError.Visibility = string.IsNullOrEmpty(DetailError.Text) ? Visibility.Collapsed : Visibility.Visible;
        LoginButton.Content = loginWindow is not null ? "查看授权进度" : a.AuthState == "ready" ? "重新授权" : "登录授权";
        LoginButton.IsEnabled = loginWindow is not null || !Service.Busy; VerifyButton.IsEnabled = !Service.Busy && a.Enabled;
        RechargeDetail.Text = $"订阅充值：{DateText(a.RechargeDate,a.RechargeTime)}\n订阅到期：{DateText(a.SubscriptionDate,a.SubscriptionTime)}";
    }

    private static void SetLoginField(TextBox field, Button copy, string? value)
    {
        var present = !string.IsNullOrEmpty(value);
        field.Text = present ? value! : "无"; field.Tag = present ? value : null; copy.IsEnabled = present;
    }
    private static void SetSecretField(TextBox field, Button copy, Func<string> reveal)
    {
        try { SetLoginField(field, copy, reveal()); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
        { field.Text = "无法解密"; field.Tag = null; copy.IsEnabled = false; }
    }
    private void CopyLoginField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox field } && field.Tag is string value)
            SafeClipboard.Copy(value, field != EmailDisplay);
    }
    private async Task DesktopAction(Func<DesktopSwitcher, AccountRecord, CancellationToken, Task> action)
    {
        if (Selected is not { } account || app.DesktopSwitch is not { } desktop || Service.Busy) return;
        try { await Service.RunDesktopOperationAsync(account, token => action(desktop, account, token)); }
        catch (Exception error)
        {
            Inform(error is InvalidOperationException or InvalidDataException ? error.Message : "操作未完成，请检查登录存储与目录权限后重试。");
        }
        finally { RenderDetails(); }
    }
    private void AuthorizeDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (loginWindow is not null) { loginWindow.WindowState = WindowState.Normal; loginWindow.Activate(); return; }
        if (Selected is not { } account || app.DesktopSwitch is not { } desktop || Service.Busy) return;
        ShowLoginWindow(new LoginWindow(account, async (method, present, token) =>
        {
            LoginResult? result = null;
            await Service.RunDesktopOperationAsync(account, async shutdown =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
                result = await desktop.AuthorizeAsync(account, method, present, linked.Token);
            });
            return result!;
        }, uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), desktopLogin: true));
    }
    private async void SwitchDesktop_Click(object sender, RoutedEventArgs e) => await DesktopAction((desktop, account, token) => desktop.SwitchAsync(account, Service.Data.Accounts, Service.Data.Settings.LimitId, token));
    private async void RestoreDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (app.DesktopSwitch is not { } desktop || Service.Busy) return;
        try { await Service.RunDesktopOperationAsync(Selected, desktop.RestoreAsync); }
        catch (Exception error) { Inform(error is InvalidOperationException or InvalidDataException ? error.Message : "恢复尚未完成，原恢复记录已保留。"); }
        finally { RenderDetails(); }
    }
    private async void KeepDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (app.DesktopSwitch is not { } desktop || Service.Busy) return;
        try { await Service.RunDesktopOperationAsync(Selected, token => desktop.KeepCurrentAsync(Service.Data.Accounts, token)); }
        catch (Exception error) { Inform(error is InvalidOperationException or InvalidDataException ? error.Message : "操作尚未完成，原恢复记录已保留。"); }
        finally { RenderDetails(); }
    }
    private void Account_Selected(object sender, SelectionChangedEventArgs e) { if (rendering) return; selectedId = (AccountsGrid.SelectedItem as AccountRow)?.Record.Id; RenderDetails(); }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Service.Busy) { Inform("请先完成当前查询或授权。"); return; }
        if (Service.Data.Accounts.Count >= 500) { Inform("最多保存 500 个账号。"); return; }
        var account = new AccountRecord();
        if (new AccountEditor(account, true, Service.AddAccount) { Owner = this }.ShowDialog() == true)
        { selectedId = account.Id; Render(); }
    }
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Selected is { } a && !Service.Busy) new AccountEditor(LocalStore.CopyAccount(a), false, Service.UpdateAccount) { Owner = this }.ShowDialog(); }
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (loginWindow is not null) { loginWindow.WindowState = WindowState.Normal; loginWindow.Activate(); return; }
        if (Selected is not { } a || Service.Busy) return;
        ShowLoginWindow(new LoginWindow(Service, a));
    }
    internal void ShowLoginWindow(LoginWindow dialog)
    {
        loginWindow = dialog; dialog.Owner = this;
        dialog.Closed += (_, _) => { loginWindow = null; RenderDetails(); };
        // Modeless: users must be able to copy credentials from the dashboard during OAuth.
        dialog.Show(); RenderDetails();
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        try { if (Selected is { } a) await Service.RefreshAsync(a); }
        catch { Inform("核验暂未完成，请稍后重试。"); }
    }
    private async void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        try { await Service.RefreshAllAsync(true); }
        catch { Inform("核验暂未完成，请稍后重试。"); }
    }
    private void RetrySave_Click(object sender, RoutedEventArgs e) { if (!Service.Busy) Service.Save(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(app) { Owner = this }.ShowDialog();
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a || Service.Busy) return;
        if (app.DesktopSwitch?.HasPending == true) { Inform("请先恢复未完成的桌面切换，再删除账号。"); return; }
        if (MessageBox.Show(this, $"删除「{a.Name}」的本地信息、密码与历史，并注销此工具保存的授权？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            // A failed deletion may require saving this local cache again, but it must
            // never leave orphaned desktop credentials after deleting an account row.
            app.DesktopSwitch?.RemoveSaved(a);
            await Service.RemoveAsync(a); selectedId = null; Render();
        }
        catch (Exception ex) { Inform(ex.Message); }
    }
    private IntPtr HandleHotkey(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == DashboardHotkeyId) { app.ToggleDashboard(); handled = true; }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    private void Inform(string message) => MessageBox.Show(this, message, "额度管理", MessageBoxButton.OK, MessageBoxImage.Information);
    public static string DateText(DateOnly? date, TimeOnly? time) => date is { } day ? day.ToDateTime(time ?? new TimeOnly(12,0)).ToString("yyyy/MM/dd HH:mm") : "无";
    public static string Percent(double? percent) => percent is null ? "未知" : percent is > 0 and < .1 ? "<0.1%" : percent.Value.ToString("0.#") + "%";
}

public sealed class AccountRow
{
    public AccountRecord Record { get; }
    public QuotaDecision Decision { get; }
    public string Name => Record.Name;
    public string Email => string.IsNullOrEmpty(Record.Email) ? "尚未授权" : Record.Email;
    public string Plan => Record.Plan is "unknown" or "" ? "未授权" : Record.Plan.ToUpperInvariant();
    public string Credits => Record.Snapshot?.ResetCount is { } count ? $"重置卡 {count} 张" : "重置卡 未知";
    public IReadOnlyList<ResetCreditExpiryRow> CreditExpiryRows { get; }
    public string CreditExpiryNotice { get; }
    public double? ShortRemaining { get; }
    public double? WeekRemaining { get; }
    public string ShortReset { get; }
    public string WeekReset { get; }
    public double SnapshotOpacity => Decision.State is Availability.Stale or Availability.Error or Availability.LoginRequired or Availability.Disabled ? 0.5 : 1;
    public string ShortText { get; }
    public string WeekText { get; }
    public double ShortValue { get; }
    public double WeekValue { get; }
    public string Status { get; }
    public string Synced { get; }
    public Brush StatusBrush { get; }
    public AccountRow(AccountRecord account, string pool, DateTimeOffset now, bool busy)
    {
        Record = account; Decision = QuotaPolicy.Evaluate(account, pool, now);
        var availableCredits = account.Snapshot?.ResetCredits?
            .Where(c => string.Equals(c.Status, "available", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).ToList() ?? [];
        CreditExpiryRows = availableCredits.Select((c, i) => new ResetCreditExpiryRow($"第 {i + 1} 张",
            c.ExpiresAt is { } expiry ? expiry.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "到期时间待官方返回")).ToList();
        CreditExpiryNotice = account.Snapshot?.ResetCount switch
        {
            0 when availableCredits.Count == 0 => "暂无可用重置卡。",
            { } count when count > availableCredits.Count => $"官方显示剩余 {count} 张，目前仅返回 {availableCredits.Count} 张的到期明细。",
            { } count when count < availableCredits.Count => "张数与明细暂不一致，请点击“立即核验”。",
            null => "官方暂未返回完整的重置卡数量与到期明细。",
            _ => ""
        };
        var bucket = account.Snapshot?.Buckets.GetValueOrDefault(pool);
        var s = bucket?.ForDuration(300)?.Remaining; var w = bucket?.ForDuration(10080)?.Remaining;
        ShortRemaining = s; WeekRemaining = w;
        string ResetText(QuotaWindow? window) => window?.ResetsAt is { } at && at > now
            ? $"刷新 {at.ToLocalTime():MM/dd HH:mm}" : window?.ResetsAt is not null ? "刷新时间已到 · 待核验" : "刷新时间待官方返回";
        ShortReset = ResetText(bucket?.ForDuration(300)); WeekReset = ResetText(bucket?.ForDuration(10080));
        ShortText = MainWindow.Percent(s); WeekText = MainWindow.Percent(w); ShortValue = s ?? 0; WeekValue = w ?? 0;
        Status = busy ? "正在核验 / 授权…" : Decision.Label;
        Synced = account.LastSuccessUtc is { } last ? last.ToLocalTime().ToString("MM/dd HH:mm") : "从未同步";
        StatusBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(busy ? "#85BBFF" : Decision.CanRecommend ? "#8ED3B2" : Decision.State is Availability.Exhausted or Availability.PendingReset ? "#E7BA8C" : "#B0B7C5"));
    }
}

public sealed record ResetCreditExpiryRow(string Label, string Expires);
