using System.Net;
using System.Text;

namespace FakeLlamaServer;

/// <summary>
/// 假的 llama-server（仅用于测试）。
///
/// 它模拟真实 llama-server 的两类行为，以便在不损坏真实环境的前提下验证
/// 手册 §3.3 的 OOM 回退循环：
///
///   1. 前 <c>fake_oom_count.txt</c> 次启动：向 **stderr** 打印 "CUDA out of memory"
///      后退出 —— 与真实 CUDA 版显存不足时的表现一致（主程序正是靠解析 stderr 检测）；
///   2. 之后的启动：真的在 <c>--port</c> 上起一个 HTTP 服务，
///      <c>/health</c> 返回 <c>{"status":"ok"}</c>，从而让主程序认为"这一次起来了"。
///
/// 每次启动都把自己收到的 <c>-ngl</c> 追加进 <c>fake_ngl_log.txt</c>，
/// 这样验证程序就能断言回退步长是否真的按 -4 递减。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var attemptsFile = Path.Combine(baseDir, "fake_attempts.txt");
        var oomCountFile = Path.Combine(baseDir, "fake_oom_count.txt");
        var nglLogFile = Path.Combine(baseDir, "fake_ngl_log.txt");

        var ngl = ParseIntArg(args, "-ngl", -1);
        var port = ParseIntArg(args, "--port", 8080);
        var contextSize = ParseIntArg(args, "-c", 0);
        var modelPath = ParseStringArg(args, "-m");

        // 启动计数
        var attempt = 1;
        try
        {
            if (File.Exists(attemptsFile) && int.TryParse(File.ReadAllText(attemptsFile).Trim(), out var previous))
            {
                attempt = previous + 1;
            }

            File.WriteAllText(attemptsFile, attempt.ToString());
        }
        catch
        {
            // 计数失败不阻塞测试
        }

        // 记录本次收到的 ngl
        try
        {
            File.AppendAllText(nglLogFile, $"{attempt}:ngl={ngl}\n", Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }

        Console.Error.WriteLine($"fake-llama: attempt={attempt} ngl={ngl} port={port} ctx={contextSize} model={modelPath}");

        var oomCount = 0;
        try
        {
            if (File.Exists(oomCountFile) && int.TryParse(File.ReadAllText(oomCountFile).Trim(), out var configured))
            {
                oomCount = configured;
            }
        }
        catch
        {
            // 忽略
        }

        if (attempt <= oomCount)
        {
            // 与真实 CUDA 版显存不足时输出到 stderr 的文本保持一致
            Console.Error.WriteLine("ggml_cuda_init: found 1 CUDA devices");
            Console.Error.WriteLine("llama_model_load: error loading model: CUDA out of memory");
            Console.Error.WriteLine("CUDA out of memory. Tried to allocate 2048.00 MiB");
            Console.Error.Flush();

            await Task.Delay(300);
            return 1;
        }

        // 这一次"成功"：起一个最小 HTTP 服务
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            Console.Error.WriteLine($"fake-llama: listening on http://127.0.0.1:{port}");
            Console.Error.Flush();

            while (true)
            {
                var context = await listener.GetContextAsync();
                var path = context.Request.Url?.AbsolutePath ?? "/";

                string body;
                int status;

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    status = 200;
                    body = "{\"status\":\"ok\"}";
                }
                else
                {
                    // 真实 llama-server 没有 /unload 与 /slots，这里如实返回 404，
                    // 以便验证主程序的"降级为终止进程"路径。
                    status = 404;
                    body = "{\"error\":\"not found\"}";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fake-llama: listener failed: {ex.Message}");
            return 2;
        }
    }

    private static int ParseIntArg(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string ParseStringArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return string.Empty;
    }
}
