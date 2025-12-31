namespace FaceSearch.Infrastructure.FastIndexing;

public sealed class FastWatchFolder
{
    public string Id { get; set; } = default!;
    public string FolderPath { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; } = true;
    public string? Note { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public bool OverwriteExisting { get; set; } = false;
    public bool CheckNote { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

