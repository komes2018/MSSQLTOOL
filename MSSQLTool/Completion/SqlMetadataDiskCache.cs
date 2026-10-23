using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MSSQLTool.Completion
{
    internal static class SqlMetadataDiskCache
    {
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSSQLTool", "MetadataCache");

        private static readonly JsonSerializer Serializer = new JsonSerializer
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        public static string GetCacheFilePath(string server, string database)
        {
            string safeServer = SanitizeName(server ?? "default_server");
            string safeDb = SanitizeName(database ?? "default_db");
            string key = ((server ?? string.Empty) + "|" + (database ?? string.Empty)).ToLowerInvariant();
            string hash;
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                hash = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 8).ToLowerInvariant();
            }
            return Path.Combine(CacheDirectory, $"{safeServer}_{safeDb}_{hash}.cache.gz");
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ' || c == '\\' || c == '/' || c == ':' || c == ',')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
        }

        public static bool TryLoad(string server, string database, out MetadataSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(server)) return false;

            try
            {
                string path = GetCacheFilePath(server, database);
                if (!File.Exists(path)) return false;

                using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    snapshot = Serializer.Deserialize<MetadataSnapshot>(jsonReader);
                }

                if (snapshot != null && snapshot.Objects != null && snapshot.Objects.Count > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Warn(ex, "Failed to load SQL metadata disk cache for " + server + "/" + database);
            }

            snapshot = null;
            return false;
        }

        public static void SaveAsync(string server, string database, MetadataSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(server) || snapshot == null || snapshot.Objects == null || snapshot.Objects.Count == 0)
                return;

            Task.Run(() =>
            {
                string tempPath = null;
                try
                {
                    Directory.CreateDirectory(CacheDirectory);
                    string targetPath = GetCacheFilePath(server, database);
                    tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");

                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest))
                    using (var writer = new StreamWriter(gzipStream, Encoding.UTF8))
                    using (var jsonWriter = new JsonTextWriter(writer))
                    {
                        Serializer.Serialize(jsonWriter, snapshot);
                    }

                    if (File.Exists(targetPath))
                    {
                        try { File.Delete(targetPath); } catch { }
                    }
                    File.Move(tempPath, targetPath);
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Warn(ex, "Failed to save SQL metadata disk cache for " + server + "/" + database);
                    try
                    {
                        if (tempPath != null && File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch { }
                }
            });
        }

        public static void Invalidate(string server, string database)
        {
            try
            {
                string path = GetCacheFilePath(server, database);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static void InvalidateAll()
        {
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    foreach (var file in Directory.GetFiles(CacheDirectory, "*.cache.gz"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        public static DateTime? QueryMaxModifyDate(SqlConnection connection)
        {
            if (connection == null) return null;
            try
            {
                if (connection.State != System.Data.ConnectionState.Open)
                    connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 5;
                    command.CommandText = @"
SELECT MAX(modify_date) 
FROM sys.objects 
WHERE type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT');";
                    object val = command.ExecuteScalar();
                    if (val != null && val != DBNull.Value && val is DateTime dt)
                    {
                        return dt;
                    }
                }
            }
            catch
            {
                // Fallback gracefully on permission or timeout
            }
            return null;
        }
    }
}
