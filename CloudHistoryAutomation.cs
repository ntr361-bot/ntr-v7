using System.Text.Json;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed record CloudHistoryRecord(
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("numbers")] string Numbers,
    [property: JsonPropertyName("special_number")] string SpecialNumber,
    [property: JsonPropertyName("special_zodiac")] string SpecialZodiac,
    [property: JsonPropertyName("open_time")] string OpenTime,
    [property: JsonPropertyName("date")] string Date);

public static class CloudHistoryAutomation
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Export(string outputFile)
    {
        DatabaseHelper.InitializeDatabase();
        CloudHistoryRecord[] records = DatabaseHelper.GetLatestHistory(int.MaxValue)
            .OrderBy(record => long.TryParse(record.Period, out long issue) ? issue : 0)
            .Select(record => new CloudHistoryRecord(record.Period, record.Numbers,
                record.SpecialNumber, record.SpecialZodiac, record.OpenTime, record.Date))
            .ToArray();
        if (records.Length == 0) throw new InvalidDataException("没有可发布的开奖记录");

        var document = new
        {
            status = "success",
            updated_at = DateTimeOffset.Now.ToString("O"),
            latest_issue = records[^1].Issue,
            records
        };
        AtomicWrite(outputFile, document);
        Console.WriteLine($"[SUCCESS] 云端开奖档案已更新：{records.Length}期，最新{records[^1].Issue}期");
        return outputFile;
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllBytes(temporary));
            if (parsed.RootElement.GetProperty("status").GetString() != "success" ||
                parsed.RootElement.GetProperty("records").GetArrayLength() == 0)
                throw new InvalidDataException("云端开奖档案校验失败");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
