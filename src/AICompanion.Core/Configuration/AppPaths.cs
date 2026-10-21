namespace AICompanion.Core.Configuration;

/// <summary>
/// 路径解析中心（准则 8：所有模型路径与资源路径来自 config.yaml，不得硬编码）。
///
/// 解析规则：配置里的相对路径一律相对于 **config.yaml 所在目录**，
/// 这样 start_all.bat 在项目根目录运行时，models/、Knowledge/、logs/ 都落在根目录下，
/// 与手册的目录布局一致；而 bin/ 输出目录在哪都不影响。
/// </summary>
public static class AppPaths
{
    private static string _root = AppContext.BaseDirectory;

    /// <summary>配置根目录（config.yaml 所在目录）的绝对路径。</summary>
    public static string Root => _root;

    public static void Configure(string configFilePath)
    {
        if (string.IsNullOrWhiteSpace(configFilePath))
        {
            return;
        }

        var full = Path.GetFullPath(configFilePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _root = dir;
        }
    }

    /// <summary>把配置里的路径解析成绝对路径。</summary>
    public static string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        var normalized = trimmed.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_root, normalized));
    }

    /// <summary>确保目录存在，返回其绝对路径。</summary>
    public static string EnsureDirectory(string? path)
    {
        var full = Resolve(path);
        if (!string.IsNullOrEmpty(full))
        {
            Directory.CreateDirectory(full);
        }

        return full;
    }
}
