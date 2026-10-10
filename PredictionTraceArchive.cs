using System.Data.SQLite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// Durable, append-only archive of genuine pre-draw Live traces and observed outcomes.
/// Never reconstructs a missing trace from later history or overwrites an issued snapshot.
/// </summary>
public static class PredictionTraceArchive
{
    private sealed class Archive
    {
        public Archive() { }
        public string SchemaVersion { get; set; } = "v1";
        public List<TraceEntry> Traces { get; set; } = new();
        public List<OutcomeEntry> Outcomes { get; set; } = new();
    }

    private sealed class TraceEntry
    {
        public TraceEntry() { }
        public string Issue { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public string PayloadHash { get; set; } = "";
    }

    private sealed class OutcomeEntry
    {
        public OutcomeEntry() { }
        public string Issue { get; set; } = "";
        public string OutcomeJson { get; set; } = "";
        public string OutcomeHash { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string Hash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static Archive Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        Archive archive = JsonSerializer.Deserialize<Archive>(gzip, Options)
            ?? throw new InvalidDataException("PredictionTrace archive is empty");
        if (archive.SchemaVersion != "v1" || archive.Traces is null || archive.Outcomes is null)
            throw new InvalidDataException("Unsupported PredictionTrace archive schema");
        if (archive.Traces.Select(x => x.Issue).Distinct(StringComparer.Ordinal).Count() != archive.Traces.Count ||
            archive.Outcomes.Select(x => x.Issue).Distinct(StringComparer.Ordinal).Count() != archive.Outcomes.Count)
            throw new InvalidDataException("Duplicate issue in PredictionTrace archive");
        foreach (TraceEntry item in archive.Traces)
        {
            if (!string.Equals(Hash(item.PayloadJson), item.PayloadHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Trace checksum mismatch: {item.Issue}");
            PredictionTraceSnapshot snapshot = JsonSerializer.Deserialize<PredictionTraceSnapshot>(item.PayloadJson, Options)
                ?? throw new InvalidDataException($"Invalid Live trace: {item.Issue}");
            if (snapshot.Issue != item.Issue || snapshot.CaptureKind != "Live" ||
                snapshot.TraceSchemaVersion != "trace-v1")
                throw new InvalidDataException($"Trace identity mismatch: {item.Issue}");
        }
        foreach (OutcomeEntry item in archive.Outcomes)
        {
            if (!string.Equals(Hash(item.OutcomeJson), item.OutcomeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Outcome checksum mismatch: {item.Issue}");
            PredictionTraceOutcome outcome = JsonSerializer.Deserialize<PredictionTraceOutcome>(item.OutcomeJson, Options)
                ?? throw new InvalidDataException($"Invalid outcome: {item.Issue}");
            if (outcome.Issue != item.Issue || !archive.Traces.Any(x => x.Issue == item.Issue))
                throw new InvalidDataException($"Orphan outcome: {item.Issue}");
        }
        return archive;
    }

    public static int Import(string path)
    {
        if (!File.Exists(path)) return 0;
        Archive archive = Read(path); // Validate every hash before touching the database.
        DatabaseHelper.InitializeDatabase();
        int added = 0;
        foreach (TraceEntry entry in archive.Traces.OrderBy(x => x.Issue, StringComparer.Ordinal))
        {
            bool existed = PredictionTraceService.GetLive(entry.Issue) is not null;
            var snapshot = JsonSerializer.Deserialize<PredictionTraceSnapshot>(entry.PayloadJson, Options)!;
            PredictionTraceService.SaveLive(snapshot); // Fails closed on an immutable hash conflict.
            if (!existed) added++;
        }

        foreach (OutcomeEntry entry in archive.Outcomes.OrderBy(x => x.Issue, StringComparer.Ordinal))
        {
            PredictionTraceOutcome outcome = JsonSerializer.Deserialize<PredictionTraceOutcome>(entry.OutcomeJson, Options)!;
            using SQLiteConnection connection = DatabaseHelper.GetConnection();
            using SQLiteTransaction transaction = connection.BeginTransaction();
            using var findTrace = new SQLiteCommand(
                "SELECT Id FROM PredictionTrace WHERE Issue=@issue AND CaptureKind='Live' AND TraceSchemaVersion='trace-v1'",
                connection, transaction);
            findTrace.Parameters.AddWithValue("@issue", entry.Issue);
            long traceId = Convert.ToInt64(findTrace.ExecuteScalar()
                ?? throw new InvalidDataException($"Missing archived Live trace: {entry.Issue}"));
            using var findOutcome = new SQLiteCommand(
                "SELECT OutcomeHash FROM PredictionTraceOutcome WHERE TraceId=@traceId",
                connection, transaction);
            findOutcome.Parameters.AddWithValue("@traceId", traceId);
            object? existingHash = findOutcome.ExecuteScalar();
            // ExecuteScalar returns null when no row exists. Convert.ToString(null)
            // returns an empty string, which must not be mistaken for a frozen outcome.
            if (existingHash is not null && existingHash is not DBNull)
            {
                if (!string.Equals(Convert.ToString(existingHash), entry.OutcomeHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Immutable outcome conflict: {entry.Issue}");
            }
            else
            {
                using var insert = new SQLiteCommand(
                    @"INSERT INTO PredictionTraceOutcome
                      (TraceId, Issue, ActualZodiac, ActualNumber, OutcomeJson, OutcomeHash, RecordedAt)
                      VALUES (@trace,@issue,@zodiac,@number,@payload,@hash,@at)",
                    connection, transaction);
                insert.Parameters.AddWithValue("@trace", traceId);
                insert.Parameters.AddWithValue("@issue", entry.Issue);
                insert.Parameters.AddWithValue("@zodiac", outcome.ActualZodiac);
                insert.Parameters.AddWithValue("@number", outcome.ActualNumber);
                insert.Parameters.AddWithValue("@payload", entry.OutcomeJson);
                insert.Parameters.AddWithValue("@hash", entry.OutcomeHash);
                insert.Parameters.AddWithValue("@at", outcome.RecordedAt.ToString("O"));
                insert.ExecuteNonQuery();
                added++;
            }
            transaction.Commit();
        }
        return added;
    }

    public static int Export(string path)
    {
        DatabaseHelper.InitializeDatabase();
        var archive = new Archive();
        using (SQLiteConnection connection = DatabaseHelper.GetConnection())
        {
            using (var command = new SQLiteCommand(
                "SELECT Issue, PayloadJson, PayloadHash FROM PredictionTrace WHERE CaptureKind='Live' ORDER BY Issue",
                connection))
            using (SQLiteDataReader reader = command.ExecuteReader())
                while (reader.Read())
                    archive.Traces.Add(new TraceEntry
                    {
                        Issue = reader.GetString(0), PayloadJson = reader.GetString(1),
                        PayloadHash = reader.GetString(2)
                    });

            using (var command = new SQLiteCommand(
                @"SELECT o.Issue, o.OutcomeJson, o.OutcomeHash FROM PredictionTraceOutcome o
                  INNER JOIN PredictionTrace t ON t.Id=o.TraceId
                  WHERE t.CaptureKind='Live' ORDER BY o.Issue", connection))
            using (SQLiteDataReader reader = command.ExecuteReader())
                while (reader.Read())
                    archive.Outcomes.Add(new OutcomeEntry
                    {
                        Issue = reader.GetString(0), OutcomeJson = reader.GetString(1),
                        OutcomeHash = reader.GetString(2)
                    });
        }

        // First validate the current database contents using the same read path.
        // Append-only: never drop an archived trace or silently replace its first payload.
        if (File.Exists(path))
        {
            Archive old = Read(path);
            foreach (TraceEntry entry in old.Traces)
                if (!archive.Traces.Any(x => x.Issue == entry.Issue &&
                    string.Equals(x.PayloadHash, entry.PayloadHash, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Archived trace disappeared or changed: {entry.Issue}");
            foreach (OutcomeEntry entry in old.Outcomes)
                if (!archive.Outcomes.Any(x => x.Issue == entry.Issue &&
                    string.Equals(x.OutcomeHash, entry.OutcomeHash, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Archived outcome disappeared or changed: {entry.Issue}");
        }

        foreach (TraceEntry entry in archive.Traces)
            if (!string.Equals(Hash(entry.PayloadJson), entry.PayloadHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Live trace checksum mismatch: {entry.Issue}");
        foreach (OutcomeEntry entry in archive.Outcomes)
            if (!string.Equals(Hash(entry.OutcomeJson), entry.OutcomeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Outcome checksum mismatch: {entry.Issue}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                JsonSerializer.Serialize(gzip, archive, Options);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return archive.Traces.Count;
    }
}
