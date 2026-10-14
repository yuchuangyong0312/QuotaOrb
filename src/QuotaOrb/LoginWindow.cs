using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuotaOrb.Services;

namespace QuotaOrb;

public sealed class LoginWindow : Window
{
    private readonly Func<LoginMethod, Action<LoginPrompt>, CancellationToken, Task<LoginResult>> login;
    private readonly Action<Uri> openBrowser;
    private CancellationTokenSource? cancel;
    private LoginPrompt? prompt;
    private bool completed;
    private bool closeAfterCancel;
    private LoginMethod? nextMethod;
    private LoginMethod currentMethod;
    private readonly bool desktopLogin;
    internal TextBlock StatusText { get; } = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = UiTheme.Muted, Margin = new Thickness(0,20,0,0) };
    internal Button CancelButton { get; } = new() { Content = "取消授权" };
    internal Button DeviceButton { get; } = new() { Content = "改用设备码授权", Margin = new Thickness(0,0,12,0) };
    internal TextBox Code { get; } = new() { IsReadOnly = true, FontSize = 22, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly StackPanel codePanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0,16,0,0) };
    internal bool IsAuthorizing => cancel is not null;

    public LoginWindow(AccountService service, AccountRecord account) : this(account,
        (method, present, token) => service.LoginAsync(account, token, method, present),
        uri => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }) { }

    // Injected in native smoke checks: no real account, browser or authorization is used.
    internal LoginWindow(AccountRecord account,
        Func<LoginMethod, Action<LoginPrompt>, CancellationToken, Task<LoginResult>> login, Action<Uri> openBrowser, bool desktopLogin = false)
    {
        this.login = login; this.openBrowser = openBrowser; this.desktopLogin = desktopLogin;
        UiTheme.Apply(this);
        Title = desktopLogin ? "授权切换登录" : "官方登录授权"; Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize; ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(24) }; Content = content;
        content.Children.Add(new TextBlock { Text = $"连接【{account.Name}】", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        content.Children.Add(StatusText);
        codePanel.Children.Add(Code);
        content.Children.Add(codePanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,28,0,0) };
        buttons.Children.Add(DeviceButton); buttons.Children.Add(CancelButton); content.Children.Add(buttons);
        CancelButton.Click += (_, _) =>
        {
            if (cancel is null) { completed = true; Close(); return; }
            nextMethod = null; closeAfterCancel = true; StopAttempt();
        };
        DeviceButton.Click += async (_, _) =>
        {
            if (cancel is null) await StartAsync(currentMethod);
            else if (prompt?.Method == LoginMethod.DeviceCode) SafeClipboard.Copy(Code.Text, true);
            else { nextMethod = LoginMethod.DeviceCode; StopAttempt(); }
        };
        Closing += (_, e) =>
        {
            if (!completed && cancel is not null && !(Application.Current is App { Exiting: true }))
            { e.Cancel = true; WindowState = WindowState.Minimized; }
        };
        Closed += (_, _) => { cancel?.Cancel(); Code.Clear(); prompt = null; };
        Loaded += async (_, _) => await StartAsync(LoginMethod.Browser);
    }

    private void StopAttempt()
    {
        CancelButton.IsEnabled = false; DeviceButton.IsEnabled = false;
        StatusText.Text = "正在结束本次授权…"; cancel?.Cancel();
    }

    private async Task StartAsync(LoginMethod method)
    {
        if (cancel is not null || completed) return;
        using var attempt = new CancellationTokenSource(); cancel = attempt;
        currentMethod = method;
        DeviceButton.Content = method == LoginMethod.Browser ? "改用设备码授权" : "复制设备码";
        DeviceButton.IsEnabled = method == LoginMethod.Browser;
        CancelButton.IsEnabled = true; CancelButton.Content = "取消授权";
        StatusText.Text = "正在准备官方授权…";
        try
        {
            var result = await login(method, ShowPrompt, attempt.Token);
            if (result.Success) { completed = true; Close(); return; }
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "本次授权已取消。旧登录网页已失效，请重新授权。"; }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
        catch { StatusText.Text = "授权暂未完成。请检查网络后重新授权。"; }
        finally
        {
            cancel = null; prompt = null; Code.Clear(); codePanel.Visibility = Visibility.Collapsed;
        }
        if (completed || Application.Current is App { Exiting: true }) return;
        if (closeAfterCancel) { completed = true; Close(); return; }
        if (nextMethod is { } next) { nextMethod = null; await StartAsync(next); return; }
        DeviceButton.Content = "重新授权"; DeviceButton.IsEnabled = true;
        CancelButton.IsEnabled = true; CancelButton.Content = "关闭";
        WindowState = WindowState.Normal;
    }

    private void ShowPrompt(LoginPrompt value)
    {
        prompt = value; Code.Text = value.UserCode ?? "";
        codePanel.Visibility = value.Method == LoginMethod.DeviceCode ? Visibility.Visible : Visibility.Collapsed;
        var completion = desktopLogin ? "完成后会核验并保存切换登录。" : "完成后会自动查询额度。";
        StatusText.Text = (value.Method == LoginMethod.Browser ? "正在等待网页授权，" : "请在官方网页输入下方设备码，") + completion;
        DeviceButton.IsEnabled = true; OpenPage();
    }

    private void OpenPage()
    {
        if (prompt is null) return;
        try { openBrowser(prompt.Url); }
        catch { throw new InvalidOperationException("未能打开默认浏览器。请检查默认浏览器设置后重新授权。"); }
    }
}
