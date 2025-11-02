namespace FaceSearch.Infrastructure.Qdrant;

public sealed class QdrantOptions
{
    // e.g. http://localhost:6333 (no trailing slash)
    public string BaseUrl { get; set; } = "http://localhost:6333";

    // default collections
    public string ClipCollection { get; set; } = "clip_512";
    public string FaceCollection { get; set; } = "faces_arcface_512";

    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 200;
}
