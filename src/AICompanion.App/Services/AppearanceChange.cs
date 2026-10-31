namespace AICompanion.App.Services;

/// <summary>
/// 「外观」页里改了哪一类设置 —— 用于把"立即生效"的请求从设置面板送到桌宠窗口。
///
/// 为什么要有这个枚举：设置面板与 MainWindow 之间只用 <see cref="AppHost.RequestAppearanceChange"/>
/// 通信，两边都不必知道对方的类型；而缩放、立绘/Live2D、不透明度在窗口里走的是**不同**的
/// 既有方法（ApplyScale / LoadAvatar / ApplyUserAppearanceSettings），所以要说明是哪一类。
///
/// 取值刻意按"要重建什么"来分，而不是按控件分：
/// 缩放要动窗口尺寸与整树变换，立绘/Live2D 要重建形象，其余几项只是重读一份偏好。
/// </summary>
public enum AppearanceChange
{
    /// <summary>气泡时长 / 透明态不透明度 / 水平微调：只重读偏好，不动形象与尺寸。</summary>
    Preferences,

    /// <summary>缩放等级：按 AppearanceStore 里的新值重新套一次倍率。</summary>
    Scale,

    /// <summary>立绘 / Live2D 开关或模型：按既有降级链重建形象。</summary>
    Avatar,

    /// <summary>全部重来（「恢复默认（清除覆盖）」按钮）。</summary>
    All,
}
