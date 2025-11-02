// ==============================
// FaceSearch.Infrastructure.Embedder
// Production-grade EmbedderClient for FastAPI service on :8090
// ==============================
// Folders (suggested):
// src/Infrastructure/Embedder/
//   IEmbedderClient.cs
//   EmbedderOptions.cs
//   Models/
//     TextEmbedModels.cs
//     ImageEmbedModels.cs
//     FaceEmbedModels.cs
//     StatusModels.cs
//   EmbedderClient.cs
//   ServiceCollectionExtensions.cs
//
// Notes
// - Uses HttpClient (DI-managed)
// - Resilient retries with exponential backoff and jitter
// - Timeouts per-request
// - Streams images from disk or byte[]; avoids loading giant files in memory repeatedly
// - Explicit CancellationToken everywhere
// - Clean Architecture friendly (Interface lives in Infrastructure but can be moved to Contracts if preferred)

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceSearch.Infrastructure.Embedder;

#region Options

public sealed class EmbedderOptions
{
    /// <summary>
    /// Base URL of the Python FastAPI embedder, e.g. "http://localhost:8090" (no trailing slash)
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8090";

    /// <summary>
    /// Per-request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Optional API key header name and value if you later secure the service.
    /// </summary>
    public string? ApiKeyHeader { get; set; }
    public string? ApiKeyValue { get; set; }

    /// <summary>
    /// Max number of retries for transient failures (HTTP 429/5xx, timeouts, IO exceptions).
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial backoff in milliseconds (exponential with jitter will be applied).
    /// </summary>
    public int BaseDelayMs { get; set; } = 250;
}

#endregion

#region Interface

public interface IEmbedderClient
{
    // CLIP text embeddings
    Task<EmbedTextResponse> EmbedTextAsync(IEnumerable<string> texts, CancellationToken ct = default);

    // CLIP image embeddings (image path or bytes)
    Task<EmbedImageResponse> EmbedImageAsync(ImageInput image, CancellationToken ct = default);

    // InsightFace face embeddings (image path or bytes)
    Task<EmbedFaceResponse> EmbedFaceAsync(ImageInput image, CancellationToken ct = default);

    // Health & diagnostics
    Task<StatusResponse> GetStatusAsync(CancellationToken ct = default);
    Task<SelfTestResponse> SelfTestAsync(CancellationToken ct = default);
}

#endregion

#region Models

public readonly record struct ImageInput
{
    public string? FilePath { get; init; }
    public ReadOnlyMemory<byte> Bytes { get; init; }
    public string? FileName { get; init; }

    public static ImageInput FromFile(string path, string? fileName = null) => new()
    {
        FilePath = path,
        Bytes = ReadOnlyMemory<byte>.Empty,
        FileName = fileName ?? System.IO.Path.GetFileName(path)
    };

    public static ImageInput FromBytes(byte[] bytes, string fileName = "upload.jpg") => new()
    {
        Bytes = new ReadOnlyMemory<byte>(bytes),
        FileName = fileName
    };
}

// ---- Text ----
public sealed class EmbedTextRequest
{
    [JsonPropertyName("texts")] public List<string> Texts { get; set; } = new();
}

public sealed class EmbedTextResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("dim")] public int Dim { get; set; }
    [JsonPropertyName("vectors")] public List<float[]> Vectors { get; set; } = new();
}

// ---- Image (CLIP) ----
public sealed class EmbedImageResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("dim")] public int Dim { get; set; }
    [JsonPropertyName("vector")] public float[] Vector { get; set; } = Array.Empty<float>();
}

// ---- Face (InsightFace) ----
public sealed class EmbedFaceResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("dim")] public int Dim { get; set; }
    [JsonPropertyName("vector")] public float[] Vector { get; set; } = Array.Empty<float>();
    [JsonPropertyName("faces_found")] public int FacesFound { get; set; }
}

// ---- Status / Selftest ----
public sealed class StatusResponse
{
    [JsonPropertyName("clip_device")] public string? ClipDevice { get; set; }
    [JsonPropertyName("face_device")] public string? FaceDevice { get; set; }
}

public sealed class SelfTestResponse
{
    public sealed class Timings
    {
        [JsonPropertyName("text_embed")] public double TextEmbedMs { get; set; }
        [JsonPropertyName("image_embed")] public double ImageEmbedMs { get; set; }
        [JsonPropertyName("face_embed")] public double? FaceEmbedMs { get; set; }
    }

    [JsonPropertyName("clip_device")] public string? ClipDevice { get; set; }
    [JsonPropertyName("face_device")] public string? FaceDevice { get; set; }
    [JsonPropertyName("timings_ms")] public Timings? Timing { get; set; }
}

#endregion


