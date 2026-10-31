using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Gpu;

/// <summary>
/// 按"显卡总显存"给出的保守预算表。
///
/// 为什么不再用一个固定的 <c>reserved_mb=4096</c>：那是按"桌面 + 浏览器 + 游戏"最坏情况拍的，
/// 小显存卡（2/3/4 GB）扣掉 4 GB 直接变负数 → 被判定"显存不足"走 CPU；
/// 而 8/12/16 GB 卡又白白让出 4 GB，能全卸载的模型只卸载一半。
/// 实测：8 GB 的 5060 跑 1.5B 被算成 ngl=14/28，只有 46.8 tok/s，
/// 而全卸载能到 182.8 tok/s。
///
/// 所以按显存档位给不同的"预留 + 安全系数"：显存越小越保守（保住系统不卡死），
/// 越大越放开（尽量全卸载）。
/// </summary>
public static class VramTiers
{
    /// <summary>一档：总显存下限（MB）、系统预留（MB）、安全系数。</summary>
    public readonly record struct Tier(int MinTotalMb, int ReservedMb, double SafetyFactor, string Label);

    /// <summary>档位表，按总显存从大到小匹配。</summary>
    private static readonly Tier[] Table =
    {
        // 阈值用 MiB 实测量级（nvidia-smi 对 8 GB 卡报 8151，不是 8192），所以各档留了余量。
        new(15800, 2048, 0.85, "16 GB"),
        new(11800, 1536, 0.84, "12 GB"),
        new(9800, 1280, 0.82, "10 GB"),
        new(7800, 1024, 0.80, "8 GB"),
        new(5800, 768, 0.75, "6 GB"),
        new(3800, 512, 0.70, "4 GB"),
        new(2800, 384, 0.65, "3 GB"),
        new(1800, 256, 0.60, "2 GB"),
        new(900, 192, 0.55, "1 GB"),
        new(0, 256, 0.60, "未知（按 2 GB 保守处理）"),
    };

    /// <summary>按总显存挑选档位。</summary>
    public static Tier For(int totalVramMb)
    {
        foreach (var tier in Table)
        {
            if (totalVramMb >= tier.MinTotalMb)
            {
                return tier;
            }
        }

        return Table[^1];
    }

    /// <summary>把整张表打印成日志（排障时能一眼看到"这台机器被归到哪档、按多少预算算"）。</summary>
    public static string DescribeTable()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var tier in Table)
        {
            if (tier.MinTotalMb == 0)
            {
                continue;
            }

            sb.Append(tier.Label).Append(": 预留 ").Append(tier.ReservedMb).Append(" MB × ")
              .Append(tier.SafetyFactor.ToString("F2")).Append("；");
        }

        return sb.ToString();
    }
}
