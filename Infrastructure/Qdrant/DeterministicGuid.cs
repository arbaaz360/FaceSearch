// Infrastructure/Qdrant/DeterministicGuid.cs
using System.Security.Cryptography;
using System.Text;

namespace FaceSearch.Infrastructure.Qdrant;

public static class DeterministicGuid
{
    // RFC-4122 style: use MD5 hash of the input string
    public static Guid FromString(string input)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
    public static string DominantPointId(this string albumId) =>
    DeterministicGuid.FromString($"dom:{albumId}").ToString();
}
