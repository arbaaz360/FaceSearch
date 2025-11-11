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

        public static float[] Mean512(this IEnumerable<float[]> vecs)
        {
            var sum = new double[512];
            int n = 0;

            foreach (var v in vecs)
            {
                if (v is null || v.Length != 512) continue;
                for (int i = 0; i < 512; i++) sum[i] += v[i];
                n++;
            }

            if (n == 0) throw new InvalidOperationException("No vectors to average");

            var mean = new float[512];
            for (int i = 0; i < 512; i++) mean[i] = (float)(sum[i] / n);

            // L2 normalize the centroid (good for cosine)
            double norm = 0;
            for (int i = 0; i < 512; i++) norm += mean[i] * mean[i];
            norm = Math.Sqrt(norm) + 1e-9;
            for (int i = 0; i < 512; i++) mean[i] = (float)(mean[i] / norm);

            return mean;
        }
    }


}
