using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace Infrastructure.Helpers
{
    public static class FileHashHelper
    {
        public static string Sha256Hex(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).ToLowerInvariant(); // 64 hex chars
        }
    }


}
