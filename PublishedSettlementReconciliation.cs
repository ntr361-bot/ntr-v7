using System.Data.SQLite;
using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// Synchronizes only settlement metadata for existing frozen prediction rows.
/// Does not generate predictions, rewrite ranks, or replay online learning.
/// </summary>
public static class PublishedSettlementReconciliation
{
    public static int Apply(string dailyRecordsDirectory)
    {
        if (!Directory.Exists(dailyRecordsDirectory)) return 0;
        DatabaseHelper.InitializeDatabase();
        var draws = DatabaseHelper.GetHistory()
            .Where(x => !string.IsNullOrWhiteSpace(x.Period))
            .GroupBy(x => x.Period, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        int changed = 0;
        foreach (string file in Directory.EnumerateFiles(dailyRecordsDirectory, "*.json")
                     .Where(path => long.TryParse(Path.GetFileNameWithoutExtension(path), out _))
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            string issue = root.GetProperty("issue").ToString();
            if (Path.GetFileName(file) != issue + ".json")
                throw new InvalidDataException($"Daily record issue/file mismatch: {file}");
            if (!root.TryGetProperty("verification", out JsonElement verification) ||
                !verification.TryGetProperty("status", out JsonElement status) ||
                status.GetString() != "verified") continue;

            string number = verification.GetProperty("actual_number").GetString()
                ?? throw new InvalidDataException($"Missing verified number: {issue}");
            string zodiac = verification.GetProperty("actual_zodiac").GetString()
                ?? throw new InvalidDataException($"Missing verified zodiac: {issue}");
            if (number.Length == 0 || zodiac.Length == 0 ||
                !draws.TryGetValue(issue, out DatabaseHelper.HistoryRecord[]? matches) ||
                matches.Length != 1 || matches[0].SpecialNumber != number ||
                matches[0].SpecialZodiac != zodiac)
                throw new InvalidDataException($"Verified daily record conflicts with original draw: {issue}");

            changed += ReconcileIssue(issue, number, zodiac);
        }
        return changed;
    }

    private static int ReconcileIssue(string issue, string number, string zodiac)
    {
        int updated = 0;
        using (SQLiteConnection connection = DatabaseHelper.GetConnection())
        {
            using SQLiteTransaction transaction = connection.BeginTransaction();
            using var select = new SQLiteCommand(
                @"SELECT Id, PredictZodiac, Top6Zodiac, FinalRankingJson,
                         ActualNumber, ActualZodiac, HitResult, Top6HitResult, ActualRank
                  FROM PredictionHistory WHERE Issue=@issue ORDER BY Id",
                connection, transaction);
            select.Parameters.AddWithValue("@issue", issue);
            var entries = new List<(long id, string top3, string top6, string ranking,
                string number, string zodiac, string hit3, string hit6, int rank)>();
            using (SQLiteDataReader reader = select.ExecuteReader())
                while (reader.Read())
                    entries.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5),
                        reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));

            foreach (var row in entries)
            {
                bool hit3 = row.top3.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains(zodiac, StringComparer.Ordinal);
                bool hit6 = row.top6.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains(zodiac, StringComparer.Ordinal);
                string result3 = hit3 ? "命中" : "未命中";
                string result6 = hit6 ? "命中" : "未命中";
                if ((!string.IsNullOrWhiteSpace(row.number) && row.number != number) ||
                    (!string.IsNullOrWhiteSpace(row.zodiac) && row.zodiac != zodiac) ||
                    (row.hit3 != "" && row.hit3 != "未开奖" && row.hit3 != result3) ||
                    (row.hit6 != "" && row.hit6 != "未开奖" && row.hit6 != result6))
                    throw new InvalidDataException($"Cannot change an already frozen settlement: {issue}, row {row.id}");

                int rank = 0;
                if (!string.IsNullOrWhiteSpace(row.ranking))
                {
                    string[] ranking = JsonSerializer.Deserialize<string[]>(row.ranking)
                        ?? throw new InvalidDataException($"Invalid frozen ranking: {issue}, row {row.id}");
                    rank = Array.IndexOf(ranking, zodiac) + 1;
                    // V7's historical issued rank can contain eleven zodiac entries.
                    // A missing actual zodiac is genuinely unranked (0); never rewrite
                    // or fabricate a twelfth item in an immutable prediction.
                    if (ranking.Length < 6 || ranking.Length > 12 ||
                        ranking.Distinct(StringComparer.Ordinal).Count() != ranking.Length)
                        throw new InvalidDataException($"Invalid frozen ranking: {issue}, row {row.id}");
                }
                if (row.rank > 0 && rank > 0 && row.rank != rank)
                    throw new InvalidDataException($"Frozen actual rank conflict: {issue}, row {row.id}");

                if (row.number == number && row.zodiac == zodiac &&
                    row.hit3 == result3 && row.hit6 == result6 && (rank == 0 || row.rank == rank))
                    continue;

                using var update = new SQLiteCommand(
                    @"UPDATE PredictionHistory
                      SET ActualNumber=@number, ActualZodiac=@zodiac,
                          HitResult=@hit3, Top6HitResult=@hit6,
                          ActualRank=CASE WHEN @rank>0 AND ActualRank=0 THEN @rank ELSE ActualRank END
                      WHERE Id=@id", connection, transaction);
                update.Parameters.AddWithValue("@number", number);
                update.Parameters.AddWithValue("@zodiac", zodiac);
                update.Parameters.AddWithValue("@hit3", result3);
                update.Parameters.AddWithValue("@hit6", result6);
                update.Parameters.AddWithValue("@rank", rank);
                update.Parameters.AddWithValue("@id", row.id);
                updated += update.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // Record an outcome only when a genuine pre-draw Live trace was archived.
        // Unknown historical learning coefficients must not be invented or replayed.
        if (PredictionTraceService.GetLive(issue) is not null)
        {
            PredictionTraceOutcome? existing = PredictionTraceService.GetLiveOutcome(issue);
            if (existing is not null)
            {
                if (existing.ActualNumber != number || existing.ActualZodiac != zodiac)
                    throw new InvalidDataException($"Immutable Live outcome conflict: {issue}");
            }
            else
            {
                var unobserved = new PredictionTraceLearningState(
                    new Dictionary<string, double>(), new Dictionary<string, double>());
                PredictionTraceService.RecordLiveOutcome(issue, zodiac, number,
                    unobserved, unobserved, false, learningObserved: false,
                    learningStatus: "SettlementOnlyNoLearningReplay");
            }
        }
        return updated;
    }
}
