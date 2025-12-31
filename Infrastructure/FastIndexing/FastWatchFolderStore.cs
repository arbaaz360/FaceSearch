using System.Text.Json;

namespace FaceSearch.Infrastructure.FastIndexing;

public static class FastWatchFolderStore
{
    public const string FileName = "watch-folders.json";

    public static string GetPath(string jobDirectory) => Path.Combine(jobDirectory, FileName);

    public static List<FastWatchFolder> Load(string jobDirectory)
    {
        var path = GetPath(jobDirectory);
        if (!File.Exists(path))
            return new List<FastWatchFolder>();

        try
        {
            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<List<FastWatchFolder>>(json) ?? new List<FastWatchFolder>();
            return items
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.FolderPath))
                .ToList();
        }
        catch
        {
            return new List<FastWatchFolder>();
        }
    }

    public static void Save(string jobDirectory, IReadOnlyCollection<FastWatchFolder> folders)
    {
        Directory.CreateDirectory(jobDirectory);
        var path = GetPath(jobDirectory);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(folders, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);

        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Copy(tmp, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }
}

