namespace FaceSearch.Infrastructure.Embedder;

public sealed class EmbedderOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8090"; // no trailing slash
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKeyHeader { get; set; }
    public string? ApiKeyValue { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 250;
    public int RetryBackoffMs { get; set; } = 200;
}
