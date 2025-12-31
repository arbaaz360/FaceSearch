namespace FaceSearch.Infrastructure.FastIndexing;

public sealed record FastVideoIndexJob(
    string FolderPath,
    bool IncludeSubdirectories,
    string? Note,
    int SampleEverySeconds = 10,
    bool KeyframesOnly = true,
    bool FemaleOnly = true,
    int MaxFacesPerVideo = 50,
    int MaxFacesPerFrame = 10,
    int MaxFrameWidth = 0,
    int MinFaceWidthPx = 40,
    double MinFaceAreaRatio = 0,
    double MinBlurVariance = 40,
    double MinDetScore = 0.6,
    double FacePadding = 0.25,
    double MaxSimilarityToExisting = 0.95,
    string? OutputDirectory = null,
    bool SaveCrops = true);
