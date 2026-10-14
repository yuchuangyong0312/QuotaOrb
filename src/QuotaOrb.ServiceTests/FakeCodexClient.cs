global using QuotaOrb.Core;
global using System.IO;
using System.Text.Json;

namespace QuotaOrb.Services;

// This test double never starts a process, opens OAuth, or accesses real credentials.
public sealed class CodexClient : IAsyncDisposable, ILoginConnection
{
    public Task Completion { get; } = new TaskCompletionSource().Task;
    private readonly string accountId;
    public static Dictionary<string, int> Queries { get; } = new();
    public static int Logouts { get; private set; }
    public static void Reset() { Queries.Clear(); Logouts = 0; }
    public event Action<string, JsonElement>? Notification { add { } remove { } }
    public CodexClient(string executable, string profile) { accountId = Path.GetFileName(profile); Directory.CreateDirectory(profile); }
    public async Task InitializeAsync(CancellationToken token) => await Task.Delay(1, token);
    public Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object response = method switch
        {
            "account/read" => new { account = new { type = "chatgpt", email = accountId + "@example.invalid", planType = "plus" } },
            "account/rateLimits/read" => new { accountId, ordinaryUsageAllowed = true, rateLimits = new { limitId = "codex", primary = new { usedPercent = 10, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() }, secondary = new { usedPercent = 20, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds() } } },
            "account/logout" => new { },
            _ => throw new InvalidOperationException("This regression test prohibits OAuth and all other RPC methods.")
        };
        if (method == "account/rateLimits/read") Queries[accountId] = Queries.GetValueOrDefault(accountId) + 1;
        if (method == "account/logout") Logouts++;
        return Task.FromResult(JsonSerializer.SerializeToElement(response));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class CodexRpcException : Exception
{
    public bool RequiresLogin => false;
    public bool Throttled => false;
}
