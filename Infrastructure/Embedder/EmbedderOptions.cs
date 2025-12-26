namespace FaceSearch.Infrastructure.Embedder;

public sealed class EmbedderOptions
{
    /// <summary>
    /// Single embedder URL (for backward compatibility). If BaseUrls is set, this is ignored.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8090"; // no trailing slash
    
    /// <summary>
    /// Multiple embedder URLs for load balancing. If set, requests will be distributed across these instances.
    /// Format: ["http://localhost:8090", "http://localhost:8091", "http://localhost:8092"]
    /// </summary>
    public string[]? BaseUrls { get; set; }
    
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKeyHeader { get; set; }
    public string? ApiKeyValue { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 250;
    public int RetryBackoffMs { get; set; } = 200;
    
    /// <summary>
    /// Load balancing strategy: "round-robin" or "random"
    /// </summary>
    public string LoadBalancingStrategy { get; set; } = "round-robin";
}
