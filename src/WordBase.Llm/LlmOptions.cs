namespace WordBase.Llm;

public sealed class LlmOptions
{
    /// <summary>OpenAI 兼容的 base url，例如 https://api.openai.com/v1 或 http://localhost:11434/v1。</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    public double Temperature { get; set; } = 0.2;

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>要求模型返回 JSON 对象。部分兼容服务不支持，会自动降级重试一次。</summary>
    public bool UseJsonMode { get; set; } = true;

    /// <summary>本地模型（Ollama/LM Studio）通常不需要 key。</summary>
    public bool HasEndpoint => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    public LlmOptions Clone() => (LlmOptions)MemberwiseClone();
}

public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public int? StatusCode { get; init; }
}

public sealed record LlmTestResult(bool Ok, string Message, string? Raw = null);
