using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AICompanion.Core.Monitor;

/// <summary>
/// GameMonitor 与主程序之间的消息类型（手册 §3.9）。
/// 手册只定义了 unload / restore / ping 三种；
/// <see cref="Cooldown"/> 是决策 Q11 新增的**反向**消息：
/// 冷却期由主程序判定，但需要约束 GameMonitor 的行为，因此必须把状态回传过去。
/// </summary>
public static class MonitorMessageTypes
{
    public const string Unload = "unload";
    public const string Restore = "restore";
    public const string Ping = "ping";

    /// <summary>主程序 → GameMonitor：某进程在指定时间前处于冷却期，不要发 unload。</summary>
    public const string Cooldown = "cooldown";
}

/// <summary>
/// 一条协议消息。字段与手册的 JSON 示例逐项对应，并做最小扩展：
///   - <see cref="Process"/>：restore 时告知"最后退出的那个游戏进程名"（决策 Q11 的绑定依据）；
///   - <see cref="Until"/>：cooldown 的截止时间；
///   - <see cref="Reason"/>：便于日志定位。
/// </summary>
public sealed class MonitorMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Process { get; set; }

    [JsonPropertyName("until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Until { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"{Type}(process={Process ?? "-"}, until={Until?.ToString("O") ?? "-"})";
}

/// <summary>
/// 协议编解码。采用 **一行一个 JSON 对象**（NDJSON）的帧格式：
/// 简单、可读、易调试，且天然避免粘包问题（按行切分即可）。
/// </summary>
public static class MonitorProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Encode(MonitorMessage message) => JsonSerializer.Serialize(message, JsonOptions) + "\n";

    public static bool TryDecode(string line, out MonitorMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "空行。";
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<MonitorMessage>(line, JsonOptions);
            if (message is null || string.IsNullOrWhiteSpace(message.Type))
            {
                error = "消息缺少 type 字段。";
                message = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败：{ex.Message}";
            return false;
        }
    }

    public static MonitorMessage Unload(string? process, string? reason = null) => new()
    {
        Type = MonitorMessageTypes.Unload,
        Process = process,
        Reason = reason,
    };

    public static MonitorMessage Restore(string? lastProcess, string? reason = null) => new()
    {
        Type = MonitorMessageTypes.Restore,
        Process = lastProcess,
        Reason = reason,
    };

    public static MonitorMessage Ping() => new() { Type = MonitorMessageTypes.Ping };

    public static MonitorMessage Cooldown(string process, DateTime until) => new()
    {
        Type = MonitorMessageTypes.Cooldown,
        Process = process,
        Until = until,
    };

    public static Encoding LineEncoding { get; } = new UTF8Encoding(false);
}
