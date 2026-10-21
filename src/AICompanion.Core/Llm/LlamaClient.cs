using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Models;

namespace AICompanion.Core.Llm;

public sealed record HealthResult(bool Healthy, int StatusCode, string Body, string? FailureReason);

public sealed record ChatResult(bool Success, string Text, string? FailureReason, bool Cancelled)
{
    public static ChatResult Fail(string reason) => new(false, string.Empty, reason, false);

    public static ChatResult Cancelled0() => new(false, string.Empty, "已取消。", true);
}

/// <summary>
/// llama-server 的 HTTP 客户端（手册 §1.5：对外部进程使用 HTTP localhost）。
///
/// 两个关键实现决策（对应决策 Q6）：
///
/// 1. **"停止生成"用断流实现**。llama.cpp 并没有 /stop 端点；
///    官方支持的取消方式是客户端断开连接（llama.cpp PR #9679：
///    "cancel prompt processing &amp; non-streamed requests when connection closed"）。
///    因此本客户端以 SSE 流式方式请求，取消时释放响应流 → 服务端立即中止本次生成。
///    另外补一次 <c>/slots?action=erase</c> 清槽，作为双保险。
///
/// 2. **HTTP 客户端禁用系统代理**。手册 §1.5 要求全部走 127.0.0.1；
///    而本机 DNS 把外部域名解析到 127.0.0.1（存在本地代理），
///    显式 UseProxy=false 可彻底避免代理设置干扰回环请求。
/// </summary>
public sealed class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IAppLogger _logger;
    private readonly string _baseUrl;

    public LlamaClient(string host, int port, IAppLogger logger, TimeSpan? healthTimeout = null)
    {
        _logger = logger.ForCategory("Llama");
        _baseUrl = $"http://{host}:{port}";

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            // 生成可能持续很久，超时由 CancellationToken 控制，这里不设整体超时。
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HealthTimeout = healthTimeout ?? TimeSpan.FromSeconds(5);
    }

    public string BaseUrl => _baseUrl;

    public TimeSpan HealthTimeout { get; }

    /// <summary>手册 §3.8 探活规则：状态码 200 且主体包含 "status":"ok"。</summary>
    public async Task<HealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HealthTimeout);

        try
        {
            using var response = await _http.GetAsync("/health", HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var ok = response.StatusCode == HttpStatusCode.OK && ContainsStatusOk(body);

            return new HealthResult(
                ok,
                (int)response.StatusCode,
                body,
                ok ? null : $"状态码 {(int)response.StatusCode}，主体 \"{Trim(body)}\"。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthResult(false, 0, string.Empty, $"探活超时（{HealthTimeout.TotalSeconds:N0} 秒）。");
        }
        catch (Exception ex)
        {
            return new HealthResult(false, 0, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 流式对话。每收到一个增量 token 就回调 <paramref name="onDelta"/>；
    /// 取消 <paramref name="cancellationToken"/> 会立即释放连接，从而让服务端停止生成。
    /// </summary>
    public async Task<ChatResult> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onDelta,
        string? grammar = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildChatPayload(messages, stream: true, grammar);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ChatResult.Fail($"HTTP {(int)response.StatusCode}：{Trim(errBody)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var builder = new StringBuilder();

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;   // 服务端正常结束流。
                }

                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                if (data == "[DONE]")
                {
                    break;
                }

                var delta = ExtractDelta(data);
                if (!string.IsNullOrEmpty(delta))
                {
                    builder.Append(delta);
                    onDelta?.Invoke(delta);
                }
            }

            return new ChatResult(true, builder.ToString(), null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常取消路径：using 会释放响应流，服务端据此停止生成。
            _logger.Info("对话生成已被取消，连接已释放（服务端将停止生成）。");
            return ChatResult.Cancelled0();
        }
        catch (Exception ex)
        {
            _logger.Error($"对话请求失败：{ex.Message}", ex);
            return ChatResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 兜底的清槽操作。llama.cpp 的槽位接口为 <c>/slots/{id}?action=erase</c>；
    /// 老版本没有该接口时会返回 404，属预期，只记录日志。
    /// </summary>
    public async Task<bool> TryEraseSlotsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync("/slots/0?action=erase", content: null, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.Info("已清空 llama-server 槽位 0。");
                return true;
            }

            _logger.Debug($"清槽返回 HTTP {(int)response.StatusCode}（该版本可能不支持 /slots，可忽略）。");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug($"清槽失败（可忽略）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尝试调用 /unload 卸载模型。
    ///
    /// 实测：llama.cpp 的单模型 llama-server **没有** /unload 端点
    /// （只有 Router Mode 下才有模型管理接口）。因此这里预期会拿到 404，
    /// 由 <see cref="LlamaServerController.UnloadAsync"/> 回退为终止进程（决策 Q6）。
    /// 保留这个调用是为了在用户换用带 /unload 的构建时能自动走上更优雅的路径。
    /// </summary>
    public async Task<bool> TryUnloadModelAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await _http.PostAsync("/unload", content: null, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.Info("已通过 /unload 卸载模型。");
                return true;
            }

            _logger.Info($"未通过 /unload 卸载模型（HTTP {(int)response.StatusCode}），将改用终止进程的方式。");
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"调用 /unload 超时（{timeout.TotalSeconds:N0} 秒），将改用终止进程的方式。");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Info($"调用 /unload 失败（{ex.Message}），将改用终止进程的方式。");
            return false;
        }
    }

    private static string BuildChatPayload(IReadOnlyList<ChatMessage> messages, bool stream, string? grammar)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(new JsonObject
            {
                ["role"] = message.Role switch
                {
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    _ => "system",
                },
                ["content"] = message.Content,
            });
        }

        var root = new JsonObject
        {
            ["messages"] = array,
            ["stream"] = stream,
            ["temperature"] = 0.7,
        };

        // 手册 §3.7 要求用 GBNF 语法约束输出为 JSON 数组。
        // llama-server 在 /v1/chat/completions 上把 grammar 作为扩展字段接受；
        // 若某个版本不接受该字段，服务端会忽略它，我们仍能在客户端做宽容解析。
        if (!string.IsNullOrWhiteSpace(grammar))
        {
            root["grammar"] = grammar;
        }

        return root.ToJsonString();
    }

    private static string? ExtractDelta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];

            // OpenAI 兼容格式：choices[0].delta.content
            if (first.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            // 少数实现把内容直接放在 text 字段。
            if (first.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ContainsStatusOk(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        // 兼容有无空格两种写法："status":"ok" 与 "status": "ok"
        var compact = body.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value)
        => value.Length <= 200 ? value : value[..200] + "...";

    public void Dispose() => _http.Dispose();
}
