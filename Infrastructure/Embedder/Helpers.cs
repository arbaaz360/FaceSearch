using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Helpers
{
    public static class DeterministicGuid
    {
        // RFC 4122-ish UUID v5 derived from arbitrary string
        public static Guid FromString(string input)
        {
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(input)); // 20 bytes

            // Take first 16 bytes for GUID
            var guid = new byte[16];
            Array.Copy(bytes, guid, 16);

            // Set version (5) and variant (RFC 4122)
            guid[6] = (byte)((guid[6] & 0x0F) | 0x50); // version 5 => upper nibble 0101xxxx
            guid[8] = (byte)((guid[8] & 0x3F) | 0x80); // variant 10xxxxxx

            return new Guid(guid);
        }
        public static bool IsGuid(this string? s)
        {
            Guid g;
            return Guid.TryParse(s, out g);
        }

    }
}
