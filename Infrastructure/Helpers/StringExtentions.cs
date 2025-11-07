using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
        public static string ToKeyValueString(this object obj)
        {
            if (obj == null) return "null";

            var sb = new StringBuilder();
            var type = obj.GetType();

            // If it's a primitive, string, or decimal — return directly
            if (type.IsPrimitive || obj is string || obj is decimal)
                return obj.ToString()!;

            // Enumerables (lists, arrays)
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var items = enumerable.Cast<object?>()
                                      .Select(o => o?.ToKeyValueString() ?? "null");
                return "[" + string.Join(", ", items) + "]";
            }

            // Objects: use reflection to get properties
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in props)
            {
                var value = p.GetValue(obj, null);
                sb.Append($"{p.Name}:{value?.ToKeyValueString() ?? "null"}, ");
            }

            // remove trailing comma+space
            if (sb.Length >= 2)
                sb.Length -= 2;

            return sb.ToString();
        }
    }


}
