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
    /*
     * 根的初始值不能天真地取 exe 目录。
     *
     * 实测事故（用户截图）：从 bin\Debug\net8.0-windows 启动、构造阶段就读素材时，
     * 相对路径全部落到 bin\Assets\Default —— 那里什么都没有，界面于是显示
     * "角色形象缺失，已使用纯色背景"，用户看到的就是一块空白。
     * 随后虽然写了"配置已加载"，但兜底背景已经画上去了。
     *
     * 所以先按"哪个目录里有 config.yaml 就用哪个"定根：优先当前工作目录，
     * 其次从 exe 目录逐级向上找（bin\Debug\net8.0-windows → 仓库根）。
     */
    private static string _root = DetectRoot();

    private static string DetectRoot()
    {
        var candidates = new List<string> { Environment.CurrentDirectory };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 6; depth++, dir = dir.Parent)
        {
            candidates.Add(dir.FullName);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(candidate, "config.yaml")))
                {
                    return candidate;
                }
            }
            catch
            {
                // 个别路径没有访问权限就跳过。
            }
        }

        return AppContext.BaseDirectory;
    }

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

}
