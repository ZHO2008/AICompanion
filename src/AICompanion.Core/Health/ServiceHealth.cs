namespace AICompanion.Core.Health;

public enum ServiceState
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Disabled = 3,
}

/// <summary>单个服务的健康快照，供设置面板与状态栏展示。</summary>
public sealed record ServiceHealthSnapshot(
    string Key,
    string DisplayName,
    ServiceState State,
    int FailCount,
    string? LastError,
    DateTime LastCheckedUtc)
{
    public string Describe() => State switch
    {
        ServiceState.Online => $"{DisplayName}：在线",
        ServiceState.Offline => $"{DisplayName}：离线（连续失败 {FailCount} 次）",
        ServiceState.Disabled => $"{DisplayName}：未启用",
        _ => $"{DisplayName}：未检测",
    };
}
