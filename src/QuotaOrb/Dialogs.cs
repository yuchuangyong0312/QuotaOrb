using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using QuotaOrb.Services;

namespace QuotaOrb;

public sealed class AccountEditor : Window
{
    public AccountEditor(AccountRecord account, bool creating, Action<AccountRecord>? commit = null)
    {
        UiTheme.Apply(this);
        Title = creating ? "添加账号" : "编辑账号登录信息"; Width = 560; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height-48; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        LocalStore.MigrateLoginInfo(account);
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var name = AddField(panel, "账号名称", new TextBox { Text = creating ? "" : account.Name, MaxLength = 60 });
        var email = AddField(panel, "登录邮箱（可留空，授权后获取）", new TextBox { Text = account.Email, MaxLength = 254, IsReadOnly = account.Snapshot is not null });
        var password = AddSecretField(panel, "登录密码（留空表示无）", () => SecretVault.Reveal(account.ProtectedPassword));
        var recharge = AddDateField(panel, "订阅充值", account.RechargeDate, account.RechargeTime);
        var expires = AddDateField(panel, "订阅到期", account.SubscriptionDate, account.SubscriptionTime);
        var factorUrl = AddSecretField(panel, "2FA 网址（留空表示无）", () => SecretVault.RevealTwoFactorUrl(account.ProtectedTwoFactorUrl));
        var factorKey = AddSecretField(panel, "2FA 密钥（留空表示无）", () => SecretVault.RevealTwoFactorKey(account.ProtectedTwoFactorKey));
        var enabled = new CheckBox { Content = "启用此账号的自动查询", IsChecked = account.Enabled }; panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "保存信息后，点击“登录授权”连接官方账号。", Foreground = UiTheme.Muted, FontSize = 12, Margin = new Thickness(0,6,0,14) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true }; var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(this,"请填写账号名称。","额度管理"); return; }
            if (!string.IsNullOrWhiteSpace(email.Text) && !email.Text.Contains('@')) { MessageBox.Show(this,"请填写登录邮箱，或留空后由授权获取。","额度管理"); return; }
            if (!TryTime(recharge.Time.Text, out var rechargeTime) || !TryTime(expires.Time.Text, out var subscriptionTime)) { MessageBox.Show(this,"时间请填写为 24 小时制 HH:mm，例如 12:00 或 18:30。","额度管理"); return; }
            if (!factorUrl.IsReadOnly && !string.IsNullOrWhiteSpace(factorUrl.Text) && (!Uri.TryCreate(factorUrl.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) { MessageBox.Show(this,"2FA 网址请填写完整的 http:// 或 https:// 地址，或留空。","额度管理"); return; }
            try
            {
                var secret = password.IsReadOnly ? account.ProtectedPassword : SecretVault.Protect(password.Text);
                var urlSecret = factorUrl.IsReadOnly ? account.ProtectedTwoFactorUrl : SecretVault.ProtectTwoFactorUrl(factorUrl.Text.Trim());
                var keySecret = factorKey.IsReadOnly ? account.ProtectedTwoFactorKey : SecretVault.ProtectTwoFactorKey(factorKey.Text.Trim());
                account.Name = name.Text.Trim(); account.Email = email.Text.Trim(); account.Enabled = enabled.IsChecked == true;
                account.ProtectedPassword = secret; account.RechargeDate = recharge.Date.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
                account.SubscriptionDate = expires.Date.SelectedDate is { } e ? DateOnly.FromDateTime(e) : null;
                account.RechargeTime = account.RechargeDate is null ? null : rechargeTime; account.SubscriptionTime = account.SubscriptionDate is null ? null : subscriptionTime;
                account.ProtectedTwoFactorUrl = urlSecret; account.ProtectedTwoFactorKey = keySecret; account.LoginInfoInitialized = true;
                commit?.Invoke(account);
                password.Clear(); DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { MessageBox.Show(this,ex.Message,"额度管理"); }
            catch { MessageBox.Show(this,"无法完成本地加密，请重试；密码尚未保存。","额度管理"); }
        };
    }
    private static TextBox AddSecretField(Panel panel, string label, Func<string> reveal)
    {
        var field = new TextBox { MaxLength = 4096, TextWrapping = TextWrapping.Wrap };
        try { field.Text = reveal(); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
        { field.Text = "无法解密，原加密内容会保留"; field.IsReadOnly = true; }
        return AddField(panel, label, field);
    }
    private static (DatePicker Date, TextBox Time) AddDateField(Panel panel, string label, DateOnly? date, TimeOnly? time)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        var picker = new DatePicker { SelectedDate = date?.ToDateTime(TimeOnly.MinValue), Margin = new Thickness(0,0,8,0) };
        var clock = new TextBox { Text = (time ?? new TimeOnly(12,0)).ToString("HH:mm"), MaxLength = 5, ToolTip = "时间（HH:mm）" };
        System.Windows.Automation.AutomationProperties.SetName(clock, label + "时间");
        Grid.SetColumn(clock,1); row.Children.Add(picker); row.Children.Add(clock); AddField(panel,label,row);
        return (picker,clock);
    }
    internal static bool TryTime(string text, out TimeOnly time) => TimeOnly.TryParseExact(string.IsNullOrWhiteSpace(text) ? "12:00" : text.Trim(), ["HH:mm","H:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
    internal static T AddField<T>(Panel panel, string title, T control) where T : UIElement
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0,10,0,5), Foreground = UiTheme.Muted, FontSize = 12 }); panel.Children.Add(control); return control;
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(App app)
    {
        UiTheme.Apply(this);
        Title = "额度管理设置"; Width = 450; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "后台与数据", FontSize = 22, FontWeight = FontWeights.SemiBold });
        var automatic = new CheckBox { Content = "自动查询已授权账号", IsChecked = app.Service.Data.Settings.AutoRefresh }; panel.Children.Add(automatic);
        var notices = new CheckBox { Content = "额度恢复、卡片与订阅到期提醒", IsChecked = app.Service.Data.Settings.Notifications }; panel.Children.Add(notices);
        var startup = new CheckBox { Content = "登录 Windows 后自动启动", IsChecked = StartupSetting.Enabled }; panel.Children.Add(startup);
        panel.Children.Add(new TextBlock { Text = $"展开窗口时每 {RefreshPolicy.ForegroundMinutes} 分钟核验，后台或最小化时每 {RefreshPolicy.BackgroundMinutes} 分钟核验。失败会延后重试。\n使用前请先安装 Codex，程序会自动查找。", FontSize = 12, Foreground = UiTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,15) });
        var export = new Button { Content = "导出账号与历史", Margin = new Thickness(0,0,0,10) }; panel.Children.Add(export);
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "额度管理备份|*.json", FileName = $"额度管理备份-{DateTime.Now:yyyyMMdd}.json" };
            if (dialog.ShowDialog(this) != true) return;
            try { app.Store.ExportAll(app.Service.Data, dialog.FileName); MessageBox.Show(this,"已导出全部账号资料与历史，包含明文密码、2FA 信息和旧备注。请妥善保管文件；导入后仍需完成官方登录授权。","额度管理"); }
            catch (InvalidDataException error) { MessageBox.Show(this,error.Message,"额度管理"); }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException) { MessageBox.Show(this,"有账号的登录信息无法解密，完整导出未完成。请使用原 Windows 用户打开后重试。","额度管理"); }
            catch { MessageBox.Show(this,"导出失败，请确认保存位置可写。","额度管理"); }
        };
        var import = new Button { Content = "导入账号与历史", Margin = new Thickness(0,0,0,10) }; panel.Children.Add(import);
        import.Click += (_, _) =>
        {
            if (app.Service.Busy) { MessageBox.Show(this,"请等当前操作结束。","额度管理"); return; }
            var dialog = new OpenFileDialog { Filter = "额度管理备份|*.json" }; if (dialog.ShowDialog(this) != true) return;
            try
            {
                var accounts = app.Store.ImportMetadata(dialog.FileName);
                var count = app.Service.ImportAccounts(accounts);
                MessageBox.Show(this,$"已导入 {count} 个账号，重复邮箱已跳过。\n导入的账号需要重新登录授权。","额度管理");
            }
            catch (InvalidOperationException ex) { MessageBox.Show(this,ex.Message,"额度管理"); }
            catch (IOException) { MessageBox.Show(this,"导入未保存，请检查文件内容、磁盘空间及目录权限后重试。","额度管理"); }
            catch { MessageBox.Show(this,"导入失败：文件格式不支持、内容不完整或数量过多。","额度管理"); }
        };
        var directory = new Button { Content = "打开本地数据目录", Margin = new Thickness(0,0,0,10) }; panel.Children.Add(directory);
        directory.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(app.Store.Root) { UseShellExecute = true }); } catch { MessageBox.Show(this,"无法打开数据目录，请稍后重试。","额度管理"); } };
        var licenses = new Button { Content = "第三方许可", Margin = new Thickness(0,0,0,10) }; panel.Children.Add(licenses);
        licenses.Click += (_, _) =>
        {
            var assembly = typeof(App).Assembly;
            var text = string.Join("\n\n",assembly.GetManifestResourceNames().Where(n=>n.StartsWith("QuotaOrb.Licenses.")).OrderBy(n=>n).Select(n=>
            { using var stream = assembly.GetManifestResourceStream(n)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }));
            new Window { Title = "第三方许可",Owner = this,Width = 760,Height = 560,WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = text,IsReadOnly = true,TextWrapping = TextWrapping.Wrap,VerticalScrollBarVisibility = ScrollBarVisibility.Auto,Margin = new Thickness(16) } }.ShowDialog();
        };
        var save = new Button { Content = "保存设置", IsDefault = true, Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,12,0,0) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            string? originalStartup = null;
            var capturedStartup = false;
            try
            {
                originalStartup = StartupSetting.Command; capturedStartup = true;
                StartupSetting.Enabled = startup.IsChecked == true;
                app.Service.UpdateSettings(new() { AutoRefresh = automatic.IsChecked == true, Notifications = notices.IsChecked == true, LimitId = app.Service.Data.Settings.LimitId });
                DialogResult = true;
            }
            catch (Exception ex)
            {
                var restored = true;
                if (capturedStartup) { try { StartupSetting.Command = originalStartup; } catch { restored = false; } }
                var message = ex is IOException or InvalidOperationException ? ex.Message : "无法保存开机启动设置，请检查当前 Windows 用户权限。";
                if (!restored) message += "\n开机启动设置恢复失败，请在设置中重新核对。";
                MessageBox.Show(this,message,"额度管理");
            }
        };
    }
}

internal static class StartupSetting
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string? Command
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue("QuotaOrb") as string; }
        set { using var key = Registry.CurrentUser.CreateSubKey(KeyPath); if (value is not null) key.SetValue("QuotaOrb",value); else key.DeleteValue("QuotaOrb",false); }
    }
    public static bool Enabled
    {
        get => Command is not null;
        set => Command = value ? $"\"{Environment.ProcessPath}\" --background" : null;
    }
}

internal static class SafeClipboard
{
    private static string? owned;
    public static async void Copy(string text, bool secret)
    {
        try
        {
            if (!secret) { Clipboard.SetText(text); return; }
            var data = new DataObject(); data.SetData(DataFormats.UnicodeText, text);
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            Clipboard.SetDataObject(data, true); owned = text;
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (owned == text) ClearOwned();
        }
        catch { MessageBox.Show("剪贴板正忙，请稍后重试。", "额度管理"); }
    }
    public static void ClearOwned()
    {
        try { if (owned is not null && Clipboard.ContainsText() && Clipboard.GetText() == owned) Clipboard.Clear(); } catch { }
        finally { owned = null; }
    }
}
