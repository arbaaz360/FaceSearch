namespace Workers.FastIndexer;

public sealed class FastIndexerOptions
{
    public string[] Folders { get; set; } = Array.Empty<string>(); // optional seed folders
    public bool IncludeSubdirectories { get; set; } = true;
    public string[] Extensions { get; set; } = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    public int BatchSize { get; set; } = 256;
    public int Parallelism { get; set; } = 8;
    public int EmbedConcurrency { get; set; } = 32;
    public int UpsertConcurrency { get; set; } = 4;
    public string Collection { get; set; } = "faces_fast_512";
    public string? Note { get; set; }
    public string JobDirectory { get; set; } = ".fast-jobs";
    public bool OverwriteExisting { get; set; } = false;
}
