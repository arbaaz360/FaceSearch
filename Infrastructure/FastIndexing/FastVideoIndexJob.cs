namespace FaceSearch.Infrastructure.FastIndexing;

public sealed record FastVideoIndexJob(
    string FolderPath,
    bool IncludeSubdirectories,
    string? Note,
    int SampleEverySeconds = 10,
    bool KeyframesOnly = true,
    int MaxFacesPerVideo = 50,
    int MaxFacesPerFrame = 3,
    int MaxFrameWidth = 640,
    int MinFaceWidthPx = 90,
    double MinFaceAreaRatio = 0.02,
    double MinBlurVariance = 80,
    double FacePadding = 0.25,
    string? OutputDirectory = null,
    bool SaveCrops = true);

