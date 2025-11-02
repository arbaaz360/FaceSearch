namespace FaceSearch.Workers.Indexer;

public sealed class IndexerOptions
{
    public int BatchSize { get; set; } = 64;
    public int IntervalSeconds { get; set; } = 5;
    public bool EnableClip { get; set; } = true;
    public bool EnableFace { get; set; } = true;
}
