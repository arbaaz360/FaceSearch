using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Infrastructure.Embedder
{
    public static class Helpers
    {
        public static string ToDeterministicUuid(this string key)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(key);
            var hash = md5.ComputeHash(bytes);
            // set UUID v3/v5 compatible bits
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant RFC 4122
            var guid = new Guid(hash);
            return guid.ToString(); // hyphenated string
        }
    }
}
