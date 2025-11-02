using System.Text.Json;

namespace FaceSearch.Mappers
{
    public static class QdrantToModelMapperHit
    {
        // ------------------------------ MAPPER -----------------------------------
        // Case/shape-agnostic extractor
        public static string? GetString(IReadOnlyDictionary<string, object?> payload, params string[] keys)
        {
            foreach (var k in keys)
            {
                // case-insensitive lookup
                var kv = payload.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase));
                if (kv.Key is null) continue;

                var v = kv.Value;
                if (v is null) return null;

                return v switch
                {
                    string s => s,
                    JsonElement j => j.ValueKind switch
                    {
                        JsonValueKind.String => j.GetString(),
                        JsonValueKind.Number => j.ToString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => j.ToString()
                    },
                    _ => v.ToString()
                };
            }
            return null;
        }
        public static Contracts.Search.SearchHit ToSearchHit(FaceSearch.Infrastructure.Qdrant.QdrantSearchHit h)
        {
            var p = h.Payload ?? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();

            return new Contracts.Search.SearchHit
            {
                ImageId = GetString(p, "imageId", "ImageId", "image_id") ?? "",
                AlbumId = GetString(p, "albumId", "AlbumId", "album_id"),
                // your payload’s full path is under "path"
                AbsolutePath = GetString(p, "path", "absolutePath", "AbsolutePath", "absolute_path") ?? "",
                SubjectId = GetString(p, "subjectId", "SubjectId", "subject_id"),
                PreviewUrl = GetString(p, "previewUrl", "PreviewUrl", "preview_url"),
                Score = h.Score
            };
        }
    }
}
