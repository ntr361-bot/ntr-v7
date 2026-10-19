using System.Text.Json.Nodes;

namespace 六合分析软件;

/// <summary>Preserve bytes and publication time when only an envelope timestamp changes.</summary>
public static class StableSnapshotFile
{
    public static bool HasSameContent(string path, string json, string timestamp)
    {
        if (!File.Exists(path)) return false;
        JsonObject previous = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"Invalid existing snapshot: {path}");
        JsonObject next = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException($"Invalid new snapshot: {path}");
        previous.Remove(timestamp);
        next.Remove(timestamp);
        return JsonNode.DeepEquals(previous, next);
    }
}
