namespace 六合分析软件;

public sealed record ZodiacRulePrediction(
    long Issue,
    string SourceIssue,
    int FirstNumber,
    int LastNumber,
    int TailSum,
    int MappedNumber,
    string MappedZodiac,
    IReadOnlyList<string> Zodiacs);

public static class ZodiacRulePredictionService
{
    private static readonly string[,] ZodiacTable =
    {
        { "鼠", "马" }, { "牛", "羊" }, { "虎", "猴" },
        { "兔", "鸡" }, { "龙", "狗" }, { "蛇", "猪" }
    };

    private static readonly Dictionary<string, (int Row, int Column)> Positions = new()
    {
        ["鼠"] = (0, 0), ["马"] = (0, 1), ["牛"] = (1, 0), ["羊"] = (1, 1),
        ["虎"] = (2, 0), ["猴"] = (2, 1), ["兔"] = (3, 0), ["鸡"] = (3, 1),
        ["龙"] = (4, 0), ["狗"] = (4, 1), ["蛇"] = (5, 0), ["猪"] = (5, 1)
    };

    public static ZodiacRulePrediction Predict(long targetIssue)
    {
        DatabaseHelper.HistoryRecord latest = DatabaseHelper.GetLatestHistory(1).FirstOrDefault()
            ?? throw new InvalidDataException("特码规律预测缺少历史数据");
        string digits = new((latest.Numbers ?? "").Where(char.IsDigit).Take(12).ToArray());
        if (digits.Length != 12)
            throw new InvalidDataException($"第{latest.Period}期平码格式无效");

        int first = int.Parse(digits[..2]);
        int last = int.Parse(digits[^2..]);
        int tailSum = first % 10 + last % 10;
        int mappedNumber = tailSum == 0 ? 49 : tailSum;
        string date = string.IsNullOrWhiteSpace(latest.OpenTime) ? latest.Date : latest.OpenTime;
        string year = date?.Length >= 4 ? date[..4] : latest.Period[..Math.Min(4, latest.Period.Length)];
        string yearPet = DatabaseHelper.GetYearPetPublic(year);
        string zodiac = DataCrawler.GetShengXiaoByTeMa(mappedNumber.ToString("D2"), yearPet);
        if (!Positions.TryGetValue(zodiac, out var position))
            throw new InvalidDataException("特码规律计算出非法生肖");

        int up = position.Row == 0 ? 5 : position.Row - 1;
        int down = position.Row == 5 ? 0 : position.Row + 1;
        int otherColumn = position.Column == 0 ? 1 : 0;
        var zodiacs = new List<string>
        {
            zodiac,
            ZodiacTable[up, 0], ZodiacTable[up, 1],
            ZodiacTable[down, 0], ZodiacTable[down, 1],
            ZodiacTable[position.Row, otherColumn]
        };

        return new ZodiacRulePrediction(targetIssue, latest.Period, first, last, tailSum,
            mappedNumber, zodiac, zodiacs.Distinct().ToArray());
    }
}
