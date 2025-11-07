namespace FaceSearch.Workers.Indexer;

public sealed class IndexerOptions
{
    public int BatchSize { get; set; } = 256;
    public int IntervalSeconds { get; set; } = 1;
    public bool EnableClip { get; set; } = true;
    public bool EnableFace { get; set; } = true;

    // ✅ make these properties so configuration binding works
    public int EmbedConcurrency { get; set; } = 4;   // Parallelism for embedding
    public int UpsertConcurrency { get; set; } = 4;  // Parallelism for Qdrant upserts
    public bool WaitForQdrant { get; set; } = true;  // Await upsert completion before marking done

    public int Parallelism { get; set; } = 4;        // Overall Parallel.ForEachAsync cap
}

