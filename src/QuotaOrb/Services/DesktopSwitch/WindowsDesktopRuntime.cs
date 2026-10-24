using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace QuotaOrb.Services;

public sealed class WindowsDesktopRuntime : IDesktopRuntime
{
    private readonly CodexInstallation installation;
    private string? applicationId;
    public string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private const string Family = "OpenAI.Codex_2p2nqsd0c76g0";
    private static readonly HashSet<string> SupportedVersions = ["26.1007.2314.0", "26.1007.9446.0", "26.1013.3656.0", "26.1013.4894.0", "26.1016.6573.0", "26.1021.3705.0"];
    public WindowsDesktopRuntime(CodexInstallation installation) => this.installation = installation;

    public async Task ValidateAsync(CancellationToken token)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var custom = Environment.GetEnvironmentVariable("CODEX_HOME", scope);
            if (!string.IsNullOrWhiteSpace(custom) && !string.Equals(Path.GetFullPath(custom), Home, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前 Codex 使用自定义登录目录，暂不支持切换。");
        }
        var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(windowsPowerShell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); Get-AppxPackage -Name OpenAI.Codex | ForEach-Object { [pscustomobject]@{Family=$_.PackageFamilyName;Version=$_.Version.ToString();Location=$_.InstallLocation} } | ConvertTo-Json -Compress");
        var text = await RunAsync(info, token);
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || QuotaParser.Text(root, "Family") != Family
                || !SupportedVersions.Contains(QuotaParser.Text(root, "Version") ?? ""))
                throw new InvalidOperationException("当前 Codex 桌面版本尚未验证，请暂时使用官方登录方式。");
            var location = QuotaParser.Text(root, "Location");
            if (string.IsNullOrWhiteSpace(location) || !File.Exists(Path.Combine(location, "app", "ChatGPT.exe")))
                throw new InvalidOperationException("未找到可启动的 Codex 桌面程序。");
        }
        catch (JsonException) { throw new InvalidOperationException("无法识别本机 Codex 桌面安装信息。"); }
        var version = await RunAsync(new ProcessStartInfo(installation.RequireExecutable(), "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }, token);
        if (version.Trim() is not ("codex-cli 0.162.0-alpha.17.2" or "codex-cli 0.163.0-alpha.10.2" or "codex-cli 0.163.0-alpha.11" or "codex-cli 0.164.0-alpha.11.1"))
            throw new InvalidOperationException("Codex 查询组件版本已变化，请先验证兼容性再切换。");
        applicationId = Family + "!App";
    }

    public void RequireStopped()
    {
        foreach (var name in new[] { "ChatGPT", "Codex", "codex-code-mode-host", "codex-command-runner" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Any(p => !p.HasExited))
                    throw new InvalidOperationException("请先从 Codex 托盘菜单退出，并关闭仍在运行的 Codex 终端或 IDE 任务，再点击切换。");
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    public async Task LaunchAsync(CancellationToken token)
    {
        if (applicationId is null) throw new InvalidOperationException("Codex 安装信息尚未验证。");
        RequireStopped();
        // Registered Windows package entry: never copy or execute WindowsApps files directly.
        using var launcher = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\" + applicationId)
        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        for (var i = 0; i < 40; i++)
        {
            token.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName("ChatGPT");
            try
            {
                foreach (var process in processes)
                    try { if (process.MainModule?.FileName.Contains("\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true) return; }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            await Task.Delay(500, token);
        }
        throw new IOException("Codex 没有正常启动。");
    }

    private static async Task<string> RunAsync(ProcessStartInfo info, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        process.Start();
        ChildProcessJob job;
        try { job = new ChildProcessJob(process); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        using var ownedJob = job;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await errors;
        if (process.ExitCode != 0) throw new IOException("无法读取 Codex 安装信息。");
        return await output;
    }
}

public sealed class DesktopLoginProbe : IDesktopLoginProbe
{
    private readonly CodexInstallation installation;
    public DesktopLoginProbe(CodexInstallation installation) => this.installation = installation;
    public async Task<QuotaSnapshot> VerifyAsync(string home, DesktopIdentity expected, CancellationToken token)
    {
        await using var client = CodexClient.ForDesktopVerification(installation.RequireExecutable(), home);
        await client.InitializeAsync(token);
        var response = await client.RequestAsync("account/read", new { refreshToken = false }, token);
        var account = QuotaParser.Get(response, "account");
        if (account is not { } who || QuotaParser.Text(who, "type") != "chatgpt"
            || !string.Equals(QuotaParser.Text(who, "email"), expected.Email, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("官方组件读取的账号与切换目标不一致。");
        var limits = await client.RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = false }, token);
        var snapshot = QuotaParser.Parse(limits, DateTimeOffset.UtcNow);
        if (snapshot.ServerAccountId != expected.AccountId || snapshot.Buckets.Count == 0)
            throw new InvalidDataException("无法确认目标账号的官方额度信息。");
        return snapshot;
    }
}
