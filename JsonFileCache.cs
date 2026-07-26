using System.Text.Json;

namespace 六合分析软件;

internal static class JsonFileCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class CacheEnvelope<T>
    {
        public string Key { get; set; } = "";
        public T? Value { get; set; }
    }

    public static bool TryLoad<T>(string name, string key, out T? value) where T : class
    {
        value = null;
        string path = GetPath(name);
        if (!File.Exists(path)) return false;

        try
        {
            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(File.ReadAllText(path), JsonOptions);
            if (envelope?.Key != key || envelope.Value == null) return false;
            value = envelope.Value;
            AppLogger.Info("缓存命中", name);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"读取缓存 {name}", ex);
            return false;
        }
    }

    public static void Save<T>(string name, string key, T value) where T : class
    {
        try
        {
            string path = GetPath(name);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new CacheEnvelope<T>
            {
                Key = key,
                Value = value
            }, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"保存缓存 {name}", ex);
        }
    }

    public static void RemoveByPrefix(string prefix)
    {
        try
        {
            foreach (string path in Directory.GetFiles(AppPaths.CacheDirectory, $"{prefix}*.json"))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"清理缓存 {prefix}", ex);
        }
    }

    private static string GetPath(string name) => Path.Combine(AppPaths.CacheDirectory, name + ".json");
}
