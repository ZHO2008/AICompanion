using System.Text;

namespace AICompanion.Core.Gpu;

/// <summary>
/// 最小 GGUF 元数据读取器。
///
/// 为什么需要它：手册 §3.3 要求 <c>ngl = max(1, min(L, ...))</c>，其中 L 是"模型实际层数"。
/// 只有拿到真实的 L 才能正确分配层数；写死 28 会在非 Qwen 模型上算错（准则 8 禁止硬编码）。
/// 因此这里直接解析 GGUF 头部，读 <c>*.block_count</c> 元数据。
///
/// 只做顺序读取、只提取一个键，不做内存映射、不加载张量数据，因此对 5GB 级模型也是毫秒级。
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

    public static bool TryReadBlockCount(string path, out int blockCount, out string? error)
    {
        blockCount = 0;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = $"模型文件不存在：{path}";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadUInt32();
            if (magic != Magic)
            {
                error = "文件头不是 GGUF 魔数，可能不是 GGUF 文件。";
                return false;
            }

            var version = reader.ReadUInt32();
            if (version is < 1 or > 3)
            {
                error = $"不支持的 GGUF 版本：{version}（本读取器支持 1-3）。";
                return false;
            }

            // v1 用 32 位长度/计数，v2 起改为 64 位。
            var wide = version >= 2;

            _ = wide ? reader.ReadUInt64() : reader.ReadUInt32();   // tensor_count，用不到
            var kvCount = wide ? reader.ReadUInt64() : reader.ReadUInt32();

            if (kvCount > MaxMetadataCount)
            {
                error = $"元数据条目数异常（{kvCount}），疑似文件损坏。";
                return false;
            }

            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader, wide);
                var type = (GgufValueType)reader.ReadUInt32();

                if (type == GgufValueType.UInt32 && key.EndsWith(".block_count", StringComparison.Ordinal))
                {
                    blockCount = (int)reader.ReadUInt32();
                    if (blockCount <= 0)
                    {
                        error = $"读取到非法层数 {blockCount}。";
                        return false;
                    }

                    return true;
                }

                SkipValue(reader, type, wide);
            }

            error = "元数据中未找到 *.block_count 键。";
            return false;
        }
        catch (EndOfStreamException)
        {
            error = "读取 GGUF 时意外到达文件末尾，文件可能不完整。";
            return false;
        }
        catch (Exception ex)
        {
            error = $"读取 GGUF 失败：{ex.Message}";
            return false;
        }
    }

    private static string ReadString(BinaryReader reader, bool wide)
    {
        var length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length > 4096)
        {
            throw new InvalidDataException($"元数据键长度异常：{length}");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static void SkipValue(BinaryReader reader, GgufValueType type, bool wide)
    {
        switch (type)
        {
            case GgufValueType.UInt8:
            case GgufValueType.Int8:
            case GgufValueType.Bool:
                reader.ReadByte();
                break;

            case GgufValueType.UInt16:
            case GgufValueType.Int16:
                reader.ReadBytes(2);
                break;

            case GgufValueType.UInt32:
            case GgufValueType.Int32:
            case GgufValueType.Float32:
                reader.ReadBytes(4);
                break;

            case GgufValueType.UInt64:
            case GgufValueType.Int64:
            case GgufValueType.Float64:
                reader.ReadBytes(8);
                break;

            case GgufValueType.String:
                var length = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                if (length > 0)
                {
                    // 分块丢弃，避免为超长字符串一次性分配。
                    var remaining = (long)length;
                    var buffer = new byte[8192];
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(buffer.Length, remaining);
                        var read = reader.Read(buffer, 0, chunk);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException();
                        }

                        remaining -= read;
                    }
                }

                break;

            case GgufValueType.Array:
                var elementType = (GgufValueType)reader.ReadUInt32();
                var count = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                if (count > MaxArrayLength)
                {
                    throw new InvalidDataException($"数组长度异常：{count}");
                }

                for (ulong i = 0; i < count; i++)
                {
                    SkipValue(reader, elementType, wide);
                }

                break;

            default:
                throw new InvalidDataException($"未知的 GGUF 值类型：{(uint)type}");
        }
    }
}
