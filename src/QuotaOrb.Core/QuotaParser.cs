using System.Text.Json;

namespace QuotaOrb.Core;

public static class QuotaParser
{
    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset fetchedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new FormatException("额度返回格式无效");
        var buckets = new Dictionary<string, QuotaBucket>(StringComparer.Ordinal);
        if (Get(result, "rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } map)
            foreach (var property in map.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object) buckets[property.Name] = Bucket(property.Value, property.Name);
        if (buckets.Count == 0 && Get(result, "rateLimits") is { ValueKind: JsonValueKind.Object } legacy)
        {
            var id = Text(legacy, "limitId") ?? "codex";
            buckets[id] = Bucket(legacy, id);
        }
        int? resetCount = null;
        List<ResetCredit>? credits = null;
        if (Get(result, "rateLimitResetCredits") is { ValueKind: JsonValueKind.Object } summary)
        {
            var count = Integer(summary, "availableCount");
            if (count >= 0 && count <= int.MaxValue) resetCount = (int)count;
            if (Get(summary, "credits") is { ValueKind: JsonValueKind.Array } rows)
                credits = rows.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                    .Select(e => new ResetCredit(Text(e, "id") ?? "", Text(e, "status"), Text(e, "resetType"), Timestamp(e, "expiresAt"), Text(e, "title"))).ToList();
        }
        return new(fetchedAt, Text(result, "accountId"), Boolean(result, "ordinaryUsageAllowed"), buckets, resetCount, credits);
    }

    private static QuotaBucket Bucket(JsonElement e, string id) => new(id, Text(e, "limitName"), Text(e, "planType"),
        Window(Get(e, "primary")), Window(Get(e, "secondary")), Text(e, "rateLimitReachedType"), Boolean(e, "spendControlReached"),
        Get(e, "individualLimit") is { ValueKind: JsonValueKind.Object } individual && Integer(individual, "remainingPercent") is { } remaining && remaining is >= int.MinValue and <= int.MaxValue ? (int)remaining : null);

    private static QuotaWindow? Window(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } e) return null;
        var used = Get(e, "usedPercent") is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? (double?)d : null;
        var duration = Integer(e, "windowDurationMins");
        return new(used, duration > 0 ? duration : null, Timestamp(e, "resetsAt"));
    }

    public static JsonElement? Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) ? value : null;
    public static string? Text(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    public static bool? Boolean(JsonElement e, string key) => Get(e, key) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static long? Integer(JsonElement e, string key) => Get(e, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : null;
    private static DateTimeOffset? Timestamp(JsonElement e, string key)
    {
        if (Integer(e, key) is not { } seconds) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return null; }
    }
}
