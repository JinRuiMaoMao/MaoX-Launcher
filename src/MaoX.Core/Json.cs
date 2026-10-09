using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaoX.Core;

/// <summary>JSON 读写辅助。动态数据（版本 JSON、API 响应）统一用 JsonNode 表示。</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonNode Parse(string text) => JsonNode.Parse(text);

    public static JsonNode Parse(byte[] data) => JsonNode.Parse(data);

    public static JsonNode ReadFile(string path) => JsonNode.Parse(File.ReadAllBytes(path));

    /// <summary>读取 JSON 文件，文件不存在或格式错误时返回 null。</summary>
    public static JsonNode TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? ReadFile(path) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteFile(string path, JsonNode node, bool pretty = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, node?.ToJsonString(pretty ? Pretty : Compact) ?? "null");
    }

    public static string Serialize(JsonNode node, bool pretty = false) =>
        node?.ToJsonString(pretty ? Pretty : Compact) ?? "null";

    // ------------------------------------------------------------------ 安全取值（类似 Python dict.get）

    public static JsonNode Get(this JsonNode node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static string Str(this JsonNode node, string key, string fallback = null)
    {
        var value = node.Get(key);
        if (value is JsonValue v)
        {
            if (v.TryGetValue(out string s))
                return s;
            return v.ToJsonString();
        }
        return fallback;
    }

    public static long Long(this JsonNode node, string key, long fallback = 0)
    {
        if (node.Get(key) is JsonValue v)
        {
            if (v.TryGetValue(out long l))
                return l;
            if (v.TryGetValue(out double d))
                return (long)d;
            if (v.TryGetValue(out string s) && long.TryParse(s, out l))
                return l;
        }
        return fallback;
    }

    public static int Int(this JsonNode node, string key, int fallback = 0) => (int)node.Long(key, fallback);

    public static double Double(this JsonNode node, string key, double fallback = 0)
    {
        if (node.Get(key) is JsonValue v)
        {
            if (v.TryGetValue(out double d))
                return d;
            if (v.TryGetValue(out string s) && double.TryParse(s, out d))
                return d;
        }
        return fallback;
    }

    public static bool Bool(this JsonNode node, string key, bool fallback = false)
    {
        if (node.Get(key) is JsonValue v && v.TryGetValue(out bool b))
            return b;
        return fallback;
    }

    /// <summary>键存在且是布尔值时返回该值，否则返回 null。</summary>
    public static bool? BoolOrNull(this JsonNode node, string key) =>
        node.Get(key) is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static JsonObject Obj(this JsonNode node, string key) => node.Get(key) as JsonObject;

    public static JsonArray Arr(this JsonNode node, string key) => node.Get(key) as JsonArray;

    /// <summary>遍历数组（键不存在时为空序列）。</summary>
    public static IEnumerable<JsonNode> Items(this JsonNode node, string key) =>
        node.Arr(key)?.Where(n => n != null) ?? [];

    public static IEnumerable<JsonNode> Items(this JsonArray array) => array?.Where(n => n != null) ?? [];

    public static string AsStr(this JsonNode node) =>
        node is JsonValue v && v.TryGetValue(out string s) ? s : node?.ToJsonString();

    public static bool IsString(this JsonNode node) => node is JsonValue v && v.TryGetValue(out string _);

    public static JsonNode Clone(this JsonNode node) => node?.DeepClone();
}
