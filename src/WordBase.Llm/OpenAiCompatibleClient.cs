using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WordBase.Llm;

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    Task<LlmTestResult> TestAsync(CancellationToken cancellationToken = default);
}

/// <summary>只依赖 OpenAI 兼容的 /chat/completions，通吃 OpenAI、DeepSeek、Ollama、LM Studio 等。</summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly bool _ownsHttpClient;

    public OpenAiCompatibleClient(LlmOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (!_options.HasEndpoint)
        {
            throw new LlmException("还没有配置大模型接口（BaseUrl / Model）。");
        }

        try
        {
            return await SendAsync(systemPrompt, userPrompt, _options.UseJsonMode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmException ex) when (_options.UseJsonMode && ex.StatusCode is 400 or 404 or 422)
        {
            // 有些兼容实现不接受 response_format，去掉后重试一次。
            return await SendAsync(systemPrompt, userPrompt, false, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<LlmTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await CompleteAsync(
                    "你是一个连通性检查助手。只回复两个字：正常。",
                    "请回复：正常",
                    cancellationToken)
                .ConfigureAwait(false);
            return new LlmTestResult(true, $"连接成功，模型返回：{Trim(reply, 80)}", reply);
        }
        catch (LlmException ex)
        {
            return new LlmTestResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new LlmTestResult(false, $"连接失败：{ex.Message}");
        }
    }

    private async Task<string> SendAsync(string systemPrompt, string userPrompt, bool jsonMode,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = _options.Temperature,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            },
        };

        if (jsonMode)
        {
            payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"请求超时（{_options.TimeoutSeconds} 秒）。本地模型可以调大超时或先预热。");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"无法连接到 {_options.BaseUrl}：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "鉴权失败，请检查 API Key。",
                    HttpStatusCode.NotFound => "接口地址不存在，请检查 BaseUrl 是否包含 /v1。",
                    HttpStatusCode.TooManyRequests => "请求过于频繁或额度不足。",
                    HttpStatusCode.BadRequest => "请求被拒绝，可能是模型名或参数不被支持。",
                    _ => "接口返回错误。",
                };
                throw new LlmException($"{hint}（HTTP {(int)response.StatusCode}）{Trim(body, 300)}")
                {
                    StatusCode = (int)response.StatusCode,
                };
            }

            var text = ExtractContent(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new LlmException("模型返回了空内容。");
            }

            return text;
        }
    }

    private static string ExtractContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private string BuildEndpoint()
    {
        var baseUrl = _options.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return baseUrl + "/chat/completions";
    }

    private static string Trim(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
