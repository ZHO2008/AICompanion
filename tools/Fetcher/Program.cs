using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Fetcher;

/// <summary>一个待下载资源：多个镜像按顺序轮换，支持断点续传。</summary>
internal sealed record Asset(
    string Name,
    string Destination,
    long ExpectedBytes,
    string[] Mirrors,
    bool RequireZipMagic = false);

internal static class Program
{
    /// <summary>单次连接无数据超过该时长即判定停滞，主动断开换镜像重试。</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>每个资源最多重试次数（每次换一个镜像）。</summary>
    private const int MaxAttempts = 40;

    private static readonly HttpClient Http = CreateClient();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var only = args.Length >= 2 && args[0] == "--only" ? args[1] : null;
        // 仓库根目录：优先 --root=<path>，其次 AICOMPANION_ROOT，最后当前工作目录。
        var root = Environment.GetEnvironmentVariable("AICOMPANION_ROOT")
                   ?? Directory.GetCurrentDirectory();

        var assets = BuildManifest(root)
            .Where(a => only is null || a.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (assets.Count == 0)
        {
            Console.WriteLine($"[fetcher] 没有匹配 \"{only}\" 的资源。");
            return 1;
        }

        var failed = new List<string>();

        foreach (var asset in assets)
        {
            var ok = await FetchAsync(asset).ConfigureAwait(false);
            if (!ok)
            {
                failed.Add(asset.Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine("================ 结果 ================");
        foreach (var asset in assets)
        {
            var exists = File.Exists(asset.Destination);
            var size = exists ? new FileInfo(asset.Destination).Length : 0;
            var status = failed.Contains(asset.Name) ? "失败" : "成功";
            Console.WriteLine($"[{status}] {asset.Name,-28} {size / 1024.0 / 1024.0,8:N1} MB  {asset.Destination}");
        }

        return failed.Count == 0 ? 0 : 2;
    }

    private static HttpClient CreateClient()
    {
        // 全程直连：本机装了 Steam++ 加速器（127.0.0.1:26561），
        // 经它转发时对 github/HF 反而更慢或直接超时，因此显式禁用代理。
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Fetcher/1.0");
        return client;
    }

    private static List<Asset> BuildManifest(string root)
    {
        var toolsSrc = Path.Combine(root, "tools", "src");
        var third = Path.Combine(root, "third_party");
        var models = Path.Combine(root, "models");

        foreach (var dir in new[]
                 {
                     toolsSrc, third, models,
                     Path.Combine(models, "asr"),
                     Path.Combine(models, "llm"),
                 })
        {
            Directory.CreateDirectory(dir);
        }

        // ghfast.top / gh-proxy.com 是 GitHub 内容的加速代理，
        // 实测分别为 ~370KB/s 与 ~110KB/s，远快于直连的 ~22KB/s。
        string[] GithubMirrors(string path) => new[]
        {
            $"https://ghfast.top/https://github.com/{path}",
            $"https://gh-proxy.com/https://github.com/{path}",
            $"https://github.com/{path}",
        };

        string[] HuggingFaceMirrors(string repoAndFile) => new[]
        {
            $"https://hf-mirror.com/{repoAndFile}",
            $"https://huggingface.co/{repoAndFile}",
        };

        return new List<Asset>
        {
            new(
                "cmake",
                Path.Combine(toolsSrc, "cmake-windows-x86_64.zip"),
                55L * 1024 * 1024,
                GithubMirrors("Kitware/CMake/releases/download/v4.4.4/cmake-4.4.4-windows-x86_64.zip"),
                RequireZipMagic: true),

            new(
                "whisper.cpp",
                Path.Combine(toolsSrc, "whisper.cpp-master.zip"),
                // 源码包只校验 ZIP 魔数，不做体积下限：
                //   GitHub 的 archive 包体积随分支内容浮动，写死预期值会误判（实测 10.2MB）。
                0,
                GithubMirrors("ggml-org/whisper.cpp/archive/refs/heads/master.zip"),
                RequireZipMagic: true),

            new(
                "llama.cpp",
                Path.Combine(toolsSrc, "llama.cpp-master.zip"),
                0,
                GithubMirrors("ggml-org/llama.cpp/archive/refs/heads/master.zip"),
                RequireZipMagic: true),

            new(
                "whisper-model",
                Path.Combine(models, "asr", "ggml-base.bin"),
                142L * 1024 * 1024,
                HuggingFaceMirrors("ggerganov/whisper.cpp/resolve/main/ggml-base.bin")),

            new(
                "qwen1.5b-model",
                Path.Combine(models, "llm", "qwen2.5-1.5b-instruct-q4_k_m.gguf"),
                1100L * 1024 * 1024,
                HuggingFaceMirrors("Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf")),
        };
    }

    private static async Task<bool> FetchAsync(Asset asset)
    {
        Console.WriteLine();
        Console.WriteLine($"========== {asset.Name} ==========");

        // 先看本地文件是否已经完好：这既省流量，也避免对已完整的文件发 Range 请求
        // 而被服务端以 416（Range Not Satisfiable）拒绝。
        if (File.Exists(asset.Destination) && Verify(asset, out _))
        {
            var size = new FileInfo(asset.Destination).Length;
            Console.WriteLine($"[skip] {asset.Name} 已存在且校验通过（{size / 1024.0 / 1024.0:N1} MB）。");
            return true;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var have = File.Exists(asset.Destination) ? new FileInfo(asset.Destination).Length : 0;
            var mirror = asset.Mirrors[(attempt - 1) % asset.Mirrors.Length];

            try
            {
                var outcome = await TryDownloadAsync(asset, mirror, have, attempt).ConfigureAwait(false);

                if (outcome == DownloadOutcome.Complete)
                {
                    if (Verify(asset, out var verifyError))
                    {
                        Console.WriteLine($"[OK  ] {asset.Name} 下载完成并校验通过。");
                        return true;
                    }

                    Console.WriteLine($"[BAD ] {asset.Name} 校验失败：{verifyError} —— 删除后重下。");
                    TryDelete(asset.Destination);
                }
            }
            catch (RangeNotSatisfiableException)
            {
                // 本地文件比服务端的还长（多半是上一轮残留），删掉从头再来。
                Console.WriteLine($"[info] {asset.Name} 服务端拒绝续传（416），删除本地残留后重下。");
                TryDelete(asset.Destination);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR ] {asset.Name} 第 {attempt} 次失败：{ex.Message}");
            }

            // 短退避，避免镜像侧限流。
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 + attempt, 20))).ConfigureAwait(false);
        }

        Console.WriteLine($"[FAIL] {asset.Name} 在 {MaxAttempts} 次尝试后仍未完成。");
        return false;
    }

    /// <summary>服务端返回 416 时抛出，用于触发"删除残留后重下"。</summary>
    private sealed class RangeNotSatisfiableException : Exception
    {
        public RangeNotSatisfiableException(string message) : base(message)
        {
        }
    }

    private enum DownloadOutcome
    {
        Complete,
        Partial,
    }

    private static async Task<DownloadOutcome> TryDownloadAsync(Asset asset, string mirror, long have, int attempt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, mirror);

        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            throw new RangeNotSatisfiableException($"HTTP {(int)response.StatusCode}");
        }

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        // 服务端忽略了 Range，返回整份内容 —— 必须从头写，否则文件会被写坏。
        var resuming = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
        if (!resuming && have > 0)
        {
            Console.WriteLine($"[info] {Path.GetFileName(mirror)} 不支持续传，从头重下。");
            have = 0;
        }

        var total = ResolveTotalLength(response, have);
        var mode = resuming ? FileMode.Append : FileMode.Create;

        await using var file = new FileStream(
            asset.Destination,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1 << 20,
            useAsync: true);

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        var buffer = new byte[1 << 20];
        var written = have;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastData = Stopwatch.StartNew();

        while (true)
        {
            // 停滞检测：连接还在但不给数据，就断开换镜像。
            if (lastData.Elapsed > StallTimeout)
            {
                Console.WriteLine($"[stall] {asset.Name} 超过 {StallTimeout.TotalSeconds:N0}s 无数据，断开重试。");
                return DownloadOutcome.Partial;
            }

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                continue;   // 单次读超时不算失败，交给停滞检测判断。
            }

            if (read <= 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            written += read;
            lastData.Restart();

            if (stopwatch.Elapsed - lastReport > TimeSpan.FromSeconds(10))
            {
                lastReport = stopwatch.Elapsed;
                var speed = written - have;
                var rate = speed / 1024.0 / 1024.0 / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                Console.WriteLine(
                    $"[{asset.Name}] {written / 1024.0 / 1024.0:N1}/{total / 1024.0 / 1024.0:N1} MB" +
                    $"  {rate:N2} MB/s  (第 {attempt} 次尝试，{new Uri(mirror).Host})");
            }
        }

        await file.FlushAsync().ConfigureAwait(false);

        var finalSize = new FileInfo(asset.Destination).Length;
        return finalSize >= total && total > 0 ? DownloadOutcome.Complete : DownloadOutcome.Partial;
    }

    private static long ResolveTotalLength(HttpResponseMessage response, long have)
    {
        // 206 时 Content-Range 形如 "bytes 100-999/1000"，末尾是总长度。
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Length is long totalFromRange)
        {
            return totalFromRange;
        }

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        return have + contentLength;
    }

    private static bool Verify(Asset asset, out string error)
    {
        error = string.Empty;

        if (!File.Exists(asset.Destination))
        {
            error = "文件不存在。";
            return false;
        }

        var size = new FileInfo(asset.Destination).Length;

        // 只做下限校验，且仅在给了预期体积时生效：
        // 不同镜像/版本的实际体积会有差异，硬比对会误报。
        if (asset.ExpectedBytes > 0 && size < asset.ExpectedBytes * 0.9)
        {
            error = $"体积偏小（{size / 1024.0 / 1024.0:N1} MB，预期约 {asset.ExpectedBytes / 1024.0 / 1024.0:N0} MB）。";
            return false;
        }

        if (asset.RequireZipMagic)
        {
            using var stream = File.OpenRead(asset.Destination);
            var magic = new byte[2];
            if (stream.Read(magic, 0, 2) < 2 || magic[0] != 0x50 || magic[1] != 0x4B)
            {
                error = "文件头不是 ZIP 魔数（PK），可能下到了错误页面。";
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略。
        }
    }
}
