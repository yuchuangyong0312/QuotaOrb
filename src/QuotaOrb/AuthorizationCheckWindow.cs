using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuotaOrb.Services;

namespace QuotaOrb;

internal sealed class AuthorizationCheckWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    internal TextBlock Progress { get; } = new() { Text = "正在准备检查…", Margin = new Thickness(0, 10, 0, 16) };
    internal Dictionary<string, TextBlock> Results { get; } = new();
    internal Button CloseButton { get; } = new() { Content = "取消检查", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
    private bool running = true;

    public AuthorizationCheckWindow(IReadOnlyList<AccountRecord> accounts, Func<Action<AuthorizationCheckResult>, CancellationToken, Task> run)
    {
        Title = "检查所有账号切换授权"; Width = 600; Height = 480; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("AppWindowStyle");
        var panel = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "所有账号的切换授权", FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Progress); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        DockPanel.SetDock(CloseButton, Dock.Bottom); panel.Children.Add(CloseButton);
        var rows = new StackPanel();
        foreach (var account in accounts)
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = account.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var result = new TextBlock { Text = "等待检查", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = (Brush)FindResource("Subtle") };
            Results.Add(account.Id, result); row.Children.Add(result);
            rows.Children.Add(new Border { Child = row, Style = (Style)FindResource("DetailCard") });
        }
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = panel; UiTheme.Apply(this);
        CloseButton.Click += (_, _) => { if (running) { cancellation.Cancel(); CloseButton.IsEnabled = false; } else Close(); };
        Closing += (_, e) => { if (running) { e.Cancel = true; cancellation.Cancel(); CloseButton.IsEnabled = false; } };
        Closed += (_, _) => cancellation.Dispose();
        Loaded += async (_, _) =>
        {
            var completed = new HashSet<string>(); var valid = 0;
            Progress.Text = $"正在检查 0 / {accounts.Count} 个账号";
            try
            {
                await run(result =>
                {
                    completed.Add(result.AccountId);
                    if (result.State == AuthorizationCheckState.Valid) valid++;
                    Results[result.AccountId].Text = result.Summary + "\n" + result.Advice;
                    Progress.Text = $"正在检查 {completed.Count} / {accounts.Count} 个账号";
                }, cancellation.Token);
                Progress.Text = $"检查完成：{completed.Count} 个账号，{valid} 个通过";
            }
            catch (OperationCanceledException) { Progress.Text = $"检查已取消，已检查 {completed.Count} / {accounts.Count} 个账号"; }
            catch { Progress.Text = "检查未完成，请稍后重试。"; }
            finally
            {
                foreach (var account in accounts.Where(a => !completed.Contains(a.Id))) Results[account.Id].Text = "本次未检查";
                running = false; CloseButton.Content = "关闭"; CloseButton.IsEnabled = true;
            }
        };
    }
}
