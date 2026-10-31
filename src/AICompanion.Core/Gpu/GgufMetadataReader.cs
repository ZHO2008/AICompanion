using System.Collections.Concurrent;
using System.Text;

namespace AICompanion.Core.Gpu;

/// <summary>
/// 最小 GGUF 元数据读取器。
///
/// 为什么需要它：手册 §3.3 要求 <c>ngl = max(1, min(L, ...))</c>，其中 L 是"模型实际层数"。
/// 只有拿到真实的 L 才能正确分配层数；写死 28 会在非 Qwen 模型上算错（准则 8 禁止硬编码）。
/// 因此这里直接解析 GGUF 头部，读 <c>*.block_count</c> 元数据。
///
/// 只做顺序读取、只提取少量键，不做内存映射、不加载张量数据，因此对 5GB 级模型也是毫秒级。
///
/// 两个关键实现约束：
///   1. **一趟解析出全部所需字段**。启动链路里 <c>TryReadModelInfo</c>（上下文/架构/专家数）
///      与 <c>TryReadBlockCount</c>（层数）以前各开一次文件、各扫一遍元数据，
///      同一个 1 GB 的 GGUF 被完整解析两次；现在两者共用同一个解析结果。
///   2. **结果按"路径 + 文件大小 + 最后写入时间"缓存**。启动只发生一次，但
///      OOM 回退重试、游戏结束后的 restore 都会再次走同一条链路，缓存把这些重复解析全部省掉；
///      文件被替换（大小/修改时间变化）时缓存自动失效，不会读到旧模型的层数。
///
/// 所有失败路径都返回 false 并给出原因，绝不向上抛异常：调用方是启动流程，
/// 一个坏模型文件不该把程序带崩。
/// </summary>
public static class GgufMetadataReader
{
    private const uint Magic = 0x46554747;   // "GGUF" 小端

    private enum GgufValueType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }

    /// <summary>安全上限：元数据 KV 数量超过该值基本可判定文件损坏或不是 GGUF。</summary>
    private const ulong MaxMetadataCount = 1_000_000;

    /// <summary>数组中单个元素的读取也设上限，防止损坏文件导致巨量循环。</summary>
    private const ulong MaxArrayLength = 10_000_000;

    /// <summary>数组最大嵌套层数（防止损坏文件用嵌套数组打爆线程栈）。</summary>
    private const int MaxArrayDepth = 32;

    /// <summary>元数据键（以及我们真正取值的短字符串）的长度上限，单位字节。</summary>
    private const int MaxStringBytes = 4096;

    /// <summary>要跳过的字符串值的长度上限（字节）。超限视为损坏，顺带堵住 ulong → long 溢出。</summary>
    private const ulong MaxSkipStringBytes = 512L * 1024 * 1024;

    /// <summary>缓存条目上限。真实场景只有 1~2 个模型文件，超了就整体清空。</summary>
    private const int CacheCapacity = 8;

    /// <summary>跳过超长字符串时的分块大小，避免为它一次性分配内存。</summary>
    private const int SkipBufferSize = 8192;

    /// <summary>一次解析得到的全部字段（读不到的字段用 null / 0 表示）。</summary>
    private readonly record struct GgufMetadata(
        string? Architecture,
        int ContextLength,
        int ExpertCount,
        int BlockCount,
        bool BlockCountFound);

    /// <summary>缓存项：文件指纹（大小 + 最后写入时间）+ 解析结果。</summary>
    private readonly record struct CacheEntry(
        long Length,
        long LastWriteTicks,
        GgufMetadata Metadata,
        string? Error);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 读模型元数据里的"架构名"和"训练上下文长度"（键名形如 <c>qwen2.context_length</c>、<c>llama.context_length</c>）。
    ///
    /// 为什么需要：上下文长度以前固定取 config 的 4096，换成长上下文模型（32k/128k）也用不到；
    /// 而换小模型时又可能开得比模型支持的上限还大，直接被 llama-server 截断。
    /// 有了这两个值，`llm.context_length: 0` 就能"按模型自动"。
    /// </summary>
    public static bool TryReadModelInfo(
        string path,
        out string? architecture,
        out int contextLength,
        out int expertCount,
        out string? error)
    {
        architecture = null;
        contextLength = 0;
        expertCount = 0;

        if (!TryReadMetadata(path, out var metadata, out error))
        {
            return false;
        }

        architecture = metadata.Architecture;
        contextLength = metadata.ContextLength;
        expertCount = metadata.ExpertCount;

        if (architecture is null && contextLength <= 0)
        {
            // 以前这里返回 false 但 error 仍是 null，日志里就成了"读不到模型元数据（）"。
            error = "元数据里既没有 general.architecture，也没有 *.context_length 键。";
            return false;
        }

        return true;
    }

    /// <summary>读 <c>*.block_count</c>（模型层数 L）。</summary>
    public static bool TryReadBlockCount(string path, out int blockCount, out string? error)
    {
        blockCount = 0;

        if (!TryReadMetadata(path, out var metadata, out error))
        {
            return false;
        }

        if (!metadata.BlockCountFound)
        {
            error = "元数据中未找到 *.block_count 键。";
            return false;
        }

        if (metadata.BlockCount <= 0)
        {
            error = $"读取到非法层数 {metadata.BlockCount}。";
            return false;
        }

        blockCount = metadata.BlockCount;
        return true;
    }

    /// <summary>
    /// 解析（或复用缓存里的）元数据。返回 true 表示文件被正确解析；
    /// 字段是否齐全由调用方按自己的语义判断。
    /// </summary>
    private static bool TryReadMetadata(string path, out GgufMetadata metadata, out string? error)
    {
        metadata = default;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "模型路径为空。";
            return false;
        }

        if (!TryReadFileStamp(path, out var length, out var ticks, out var stampError))
        {
            error = stampError;
            return false;
        }

        if (Cache.TryGetValue(path, out var cached)
            && cached.Length == length
            && cached.LastWriteTicks == ticks)
        {
            metadata = cached.Metadata;
            error = cached.Error;
            return error is null;
        }

        try
        {
            metadata = Parse(path);
        }
        catch (Exception ex)
        {
            // 失败也缓存：同一个坏文件被启动重试反复解析没有意义，
            // 而且缓存键带文件指纹，用户换掉文件后会自动重新解析。
            error = DescribeFailure(path, ex);
            Store(path, length, ticks, default, error);
            return false;
        }

        Store(path, length, ticks, metadata, null);
        return true;
    }

    private static GgufMetadata Parse(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("文件头不是 GGUF 魔数，可能不是 GGUF 文件。");
        }

        var version = reader.ReadUInt32();
        if (version is < 1 or > 3)
        {
            throw new InvalidDataException($"不支持的 GGUF 版本：{version}（本读取器支持 1-3）。");
        }

        // v1 用 32 位长度/计数，v2 起改为 64 位。
        var wide = version >= 2;

        _ = wide ? reader.ReadUInt64() : reader.ReadUInt32();   // tensor_count，用不到
        var kvCount = wide ? reader.ReadUInt64() : reader.ReadUInt32();

        if (kvCount > MaxMetadataCount)
        {
            throw new InvalidDataException($"元数据条目数异常（{kvCount}），疑似文件损坏。");
        }

        string? architecture = null;
        var contextLength = 0;
        var expertCount = 0;
        var blockCount = 0;
        var blockCountFound = false;

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(reader, wide, MaxStringBytes, "元数据键");
            var type = (GgufValueType)reader.ReadUInt32();

            if (type == GgufValueType.String
                && key.Equals("general.architecture", StringComparison.Ordinal))
            {
                architecture = ReadString(reader, wide, MaxStringBytes, "general.architecture");
                continue;
            }

            if (type == GgufValueType.UInt32)
            {
                if (key.EndsWith(".context_length", StringComparison.Ordinal))
                {
                    contextLength = ReadInt32(reader, key);
                    continue;
                }

                // MoE 模型的专家数（如 qwen2moe.expert_count / deepseek2.expert_count）。
                // 只用来在显存预算上留出余量，不需要精确。
                if (key.EndsWith(".expert_count", StringComparison.Ordinal))
                {
                    expertCount = ReadInt32(reader, key);
                    continue;
                }

                if (key.EndsWith(".block_count", StringComparison.Ordinal))
                {
                    blockCount = ReadInt32(reader, key);
                    blockCountFound = true;
                    continue;
                }
            }

            SkipValue(reader, type, wide);
        }

        return new GgufMetadata(architecture, contextLength, expertCount, blockCount, blockCountFound);
    }

    /// <summary>
    /// 读一个"计数型" UInt32 元数据。
    ///
    /// 不能直接 <c>(int)reader.ReadUInt32()</c>：损坏文件里 0xFFFFFFFF 会变成 -1，
    /// 于是"层数 -1"或"上下文 -1"被当成合法值带进显存计算（除零、负数 ngl）。
    /// 超过 int 上限一律按文件损坏处理。
    /// </summary>
    private static int ReadInt32(BinaryReader reader, string key)
    {
        var raw = reader.ReadUInt32();
        if (raw > int.MaxValue)
        {
            throw new InvalidDataException(
                $"元数据 {key} 的值 {raw} 超出可表示范围（>{int.MaxValue}），疑似文件损坏。");
        }

        return (int)raw;
    }

    private static string ReadString(BinaryReader reader, bool wide, int maxBytes, string what)
    {
        var length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > (ulong)maxBytes)
        {
            throw new InvalidDataException($"{what}长度异常：{length} 字节（上限 {maxBytes}）。");
        }

        return Encoding.UTF8.GetString(ReadExactly(reader, (int)length, what));
    }

    /// <summary>
    /// 读满指定字节数。
    ///
    /// <see cref="BinaryReader.ReadBytes(int)"/> 在文件被截断时**不抛异常**，只返回更少的字节；
    /// 直接用它会把这个坏文件当成"合法但字段奇怪"的 GGUF 解析下去。这里显式补齐检查，
    /// 让"模型没下载完"必然表现为失败而不是算出一个假层数。
    /// </summary>
    private static byte[] ReadExactly(BinaryReader reader, int count, string what)
    {
        var buffer = reader.ReadBytes(count);
        if (buffer.Length != count)
        {
            throw new EndOfStreamException(
                $"读取{what}时文件提前结束（需要 {count} 字节，实际 {buffer.Length} 字节），文件可能不完整。");
        }

        return buffer;
    }

    private static void SkipBytes(BinaryReader reader, int count)
    {
        var buffer = reader.ReadBytes(count);
        if (buffer.Length != count)
        {
            throw new EndOfStreamException(
                $"跳过 {count} 字节时文件提前结束（实际 {buffer.Length} 字节），文件可能不完整。");
        }
    }

    private static void SkipValue(BinaryReader reader, GgufValueType type, bool wide, int depth = 0)
    {
        switch (type)
        {
            case GgufValueType.UInt8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                SkipBytes(reader, 1);
                break;

            case GgufValueType.UInt16:
            case GgufValueType.Int16:
                SkipBytes(reader, 2);
                break;

            case GgufValueType.UInt32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                SkipBytes(reader, 4);
                break;

            case GgufValueType.UInt64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                SkipBytes(reader, 8);
                break;

            case GgufValueType.String:
                var length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                if (length == 0)
                {
                    break;
                }

                // 先卡上限再转 long：直接把 ulong 转成 long 会得到负数，
                // 那个负的"剩余长度"会让循环一次都不跑，等于静默跳过。
                if (length > MaxSkipStringBytes)
                {
                    throw new InvalidDataException(
                        $"元数据字符串长度异常：{length} 字节（上限 {MaxSkipStringBytes}），疑似文件损坏。");
                }

                // 分块丢弃，避免为超长字符串一次性分配。
                var remaining = (long)length;
                var buffer = new byte[SkipBufferSize];
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(buffer.Length, remaining);
                    var read = reader.Read(buffer, 0, chunk);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException(
                            $"跳过字符串值时尚有 {remaining} 字节未读到，文件可能不完整。");
                    }

                    remaining -= read;
                }

                break;

            case GgufValueType.Array:
                var elementType = (GgufValueType)reader.ReadUInt32();
                var count = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                if (count > MaxArrayLength)
                {
                    throw new InvalidDataException($"数组长度异常：{count}。");
                }

                // 深度上限：GGUF 允许嵌套数组，若文件损坏或恶意构造
                // Array<Array<...>>，递归会直接打爆线程栈（StackOverflowException 无法 catch）。
                if (depth >= MaxArrayDepth)
                {
                    throw new InvalidDataException($"数组嵌套过深（>{MaxArrayDepth} 层）。");
                }

                for (ulong i = 0; i < count; i++)
                {
                    SkipValue(reader, elementType, wide, depth + 1);
                }

                break;

            default:
                throw new InvalidDataException($"未知的 GGUF 值类型：{(uint)type}");
        }
    }

    private static bool TryReadFileStamp(string path, out long length, out long ticks, out string? error)
    {
        length = 0;
        ticks = 0;
        error = null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = $"模型文件不存在：{path}";
                return false;
            }

            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法读取模型文件信息（{path}）：{ex.Message}";
            return false;
        }
    }

    private static string DescribeFailure(string path, Exception ex)
    {
        var reason = ex switch
        {
            EndOfStreamException => $"文件不完整（{ex.Message}）",
            InvalidDataException => ex.Message,
            IOException => $"读文件失败：{ex.Message}",
            UnauthorizedAccessException => $"没有读取权限：{ex.Message}",
            _ => ex.Message,
        };

        return $"读取 GGUF 失败（{Path.GetFileName(path)}）：{reason}";
    }

    private static void Store(string path, long length, long ticks, GgufMetadata metadata, string? error)
    {
        // 只在条目数超限时整体清空：真实场景最多两三个模型文件，
        // 为了 LRU 再维护一套链表不值得。
        if (Cache.Count >= CacheCapacity && !Cache.ContainsKey(path))
        {
            Cache.Clear();
        }

        Cache[path] = new CacheEntry(length, ticks, metadata, error);
    }
}
