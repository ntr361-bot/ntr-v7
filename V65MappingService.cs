using System.Globalization;
using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// V6.5 唯一号码映射入口。生肖按农历年轮换；波色按农历年版本保存。
/// 已开奖的实际波色应优先保留网页抓取值，本地表只作为可审计的兜底。
/// </summary>
public static class V65MappingService
{
    public const string ZodiacNumberMappingVersion = "V65-zodiac-lunar-2026.01";
    public const string WaveColorMappingVersion = "V65-wave-lunar-2026.01";

    private static readonly string[] Zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    private static readonly IReadOnlyDictionary<int, string> CurrentWaveColors = new Dictionary<int, string>
    {
        [1] = "红", [2] = "红", [3] = "蓝", [4] = "蓝", [5] = "绿", [6] = "绿", [7] = "红", [8] = "红",
        [9] = "蓝", [10] = "蓝", [11] = "绿", [12] = "红", [13] = "红", [14] = "蓝", [15] = "蓝", [16] = "绿",
        [17] = "绿", [18] = "红", [19] = "红", [20] = "蓝", [21] = "绿", [22] = "绿", [23] = "红", [24] = "红",
        [25] = "蓝", [26] = "蓝", [27] = "绿", [28] = "绿", [29] = "红", [30] = "红", [31] = "蓝", [32] = "绿",
        [33] = "绿", [34] = "红", [35] = "红", [36] = "蓝", [37] = "蓝", [38] = "绿", [39] = "绿", [40] = "红",
        [41] = "蓝", [42] = "蓝", [43] = "绿", [44] = "绿", [45] = "红", [46] = "红", [47] = "蓝", [48] = "蓝", [49] = "绿"
    };

    public static IReadOnlyDictionary<int, string> NumberToWaveColor => CurrentWaveColors;

    public static string GetWaveColor(string number)
    {
        return int.TryParse(number, out int value) && CurrentWaveColors.TryGetValue(value, out string? color) ? color : "";
    }

    public static string GetYearZodiac(int lunarYear)
    {
        int index = (lunarYear - 2020) % 12;
        if (index < 0) index += 12;
        return Zodiacs[index];
    }

    public static int GetLunarYear(DateTime date)
    {
        try { return new ChineseLunisolarCalendar().GetYear(date); }
        catch (ArgumentOutOfRangeException) { return date.Year; }
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetZodiacNumberMap(int lunarYear) =>
        GetZodiacNumberMap(GetYearZodiac(lunarYear));

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetZodiacNumberMap(string yearZodiac)
    {
        string year = NormalizeZodiac(yearZodiac);
        int yearIndex = Array.IndexOf(Zodiacs, year);
        if (yearIndex < 0) return new Dictionary<string, IReadOnlyList<string>>();
        string[] order = Enumerable.Range(0, 12).Select(offset => Zodiacs[(yearIndex - offset + 12) % 12]).ToArray();
        var map = Zodiacs.ToDictionary(zodiac => zodiac, _ => new List<string>());
        for (int number = 1; number <= 48; number++) map[order[(number - 1) % 12]].Add(number.ToString("D2"));
        map[year].Add("49");
        return map.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value);
    }

    public static string GetZodiacBySpecialNumber(string number, int lunarYear) =>
        GetZodiacBySpecialNumber(number, GetYearZodiac(lunarYear));

    public static string GetZodiacBySpecialNumber(string number, string yearZodiac)
    {
        string normalized = int.TryParse(number, out int value) && value is >= 1 and <= 49 ? value.ToString("D2") : "";
        if (string.IsNullOrEmpty(normalized)) return "";
        return GetZodiacNumberMap(yearZodiac).FirstOrDefault(pair => pair.Value.Contains(normalized)).Key ?? "";
    }

    public static string CreateSnapshot(string targetIssue, DateTime targetDate)
    {
        int lunarYear = GetLunarYear(targetDate);
        return JsonSerializer.Serialize(new
        {
            targetIssue,
            targetDate = targetDate.ToString("yyyy-MM-dd"),
            lunarYear,
            yearZodiac = GetYearZodiac(lunarYear),
            zodiacNumberMappingVersion = ZodiacNumberMappingVersion,
            waveColorMappingVersion = WaveColorMappingVersion,
            waveColorSource = "本地农历年快照（已开奖记录应以网页抓取值为准）"
        });
    }

    private static string NormalizeZodiac(string value) => value.Trim() switch
    {
        "龍" => "龙", "蛇" => "蛇", "馬" => "马", "羊" => "羊", "猴" => "猴", "雞" => "鸡", "狗" => "狗", "豬" => "猪",
        "鼠" => "鼠", "牛" => "牛", "虎" => "虎", "兔" => "兔", "龙" => "龙", "马" => "马", "鸡" => "鸡", "猪" => "猪",
        var other => other
    };
}
