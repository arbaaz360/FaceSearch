using System.Text.Json.Serialization;

namespace FaceSearch.Infrastructure.Embedder
{
    // ---- Image / Face input ----
    public sealed class ImageInput
    {
        // FastAPI expects "path" or "image_base64" (lowercase!)
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; init; }

        [JsonPropertyName("image_base64")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageBase64 { get; init; }

        // Helpers
        public static ImageInput FromFile(string path) => new() { Path = path };

        public static ImageInput FromBytes(byte[] bytes) =>
            new() { ImageBase64 = Convert.ToBase64String(bytes) };
    }

    // ---- Text ----
    public sealed class EmbedTextRequest
    {
        // Must be "text", singular
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public sealed class EmbedTextResponse
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    // ---- Image (CLIP) ----
    public sealed class EmbedImageResponse
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = Array.Empty<float>();
    }

    // ---- Face (InsightFace) ----
    public sealed class EmbedFaceResponse
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = Array.Empty<float>();

        // Optional, only if your server returns it. Keep nullable.
        [JsonPropertyName("faces_found")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int? FacesFound { get; set; }
    }

    // ---- Status / Selftest ----
    public sealed class StatusResponse
    {
        public string? Status { get; set; }
        [JsonPropertyName("clip_device")] public string? ClipDevice { get; set; }
        [JsonPropertyName("face_device")] public string? FaceDevice { get; set; }
    }

    public sealed class SelfTestResponse
    {
        public sealed class Timings
        {
            [JsonPropertyName("text_embed")] public double? TextEmbedMs { get; set; }
            [JsonPropertyName("image_embed")] public double? ImageEmbedMs { get; set; }
            [JsonPropertyName("face_embed")] public double? FaceEmbedMs { get; set; }
           
        }

        [JsonPropertyName("clip_device")] public string? ClipDevice { get; set; }
        [JsonPropertyName("face_device")] public string? FaceDevice { get; set; }
        [JsonPropertyName("timings_ms")] public Timings? Timing { get; set; }
        public bool Passed { get; set; }
        public string? Details { get; set; }
    }
}
