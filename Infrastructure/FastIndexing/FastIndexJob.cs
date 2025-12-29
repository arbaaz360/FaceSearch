namespace FaceSearch.Infrastructure.FastIndexing;

public sealed record FastIndexJob(
    string FolderPath,
    bool IncludeSubdirectories,
    string? Note,
    bool OverwriteExisting = false,
    bool CheckNote = true);
