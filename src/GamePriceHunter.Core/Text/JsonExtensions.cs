using System.Globalization;
using System.Text.Json;

namespace GamePriceHunter.Core.Text;

/// <summary>Null-tolerant accessors for loosely shaped store JSON.</summary>
public static class JsonExtensions
{
    /// <summary>Property of an object, or null when missing / not an object / JSON null.</summary>
    public static JsonElement? Prop(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value;
    }

    /// <summary>Walks a dotted path; numeric segments index arrays: "data.items.0.name".</summary>
    public static JsonElement? Path(this JsonElement element, string path)
    {
        JsonElement? current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current is not { } node)
                return null;

            if (node.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index))
                current = index >= 0 && index < node.GetArrayLength() ? node[index] : null;
            else
                current = node.Prop(segment);
        }
        return current;
    }

    public static string? Str(this JsonElement element, string path)
    {
        var value = element.Path(path);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static decimal? Dec(this JsonElement element, string path)
    {
        var value = element.Path(path);
        switch (value?.ValueKind)
        {
            case JsonValueKind.Number when value.Value.TryGetDecimal(out var number):
                return number;
            case JsonValueKind.String when decimal.TryParse(value.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    public static long? Long(this JsonElement element, string path)
    {
        var number = element.Dec(path);
        return number is null ? null : (long)number.Value;
    }

    public static bool? Bool(this JsonElement element, string path)
    {
        var value = element.Path(path);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.Value.TryGetInt32(out var n) ? n != 0 : null,
            JsonValueKind.String => bool.TryParse(value.Value.GetString(), out var b) ? b : value.Value.GetString() is "1" ? true : value.Value.GetString() is "0" ? false : null,
            _ => null,
        };
    }

    /// <summary>Array items at the path, or nothing.</summary>
    public static IEnumerable<JsonElement> Arr(this JsonElement element, string path)
    {
        var value = element.Path(path);
        return value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    }

    /// <summary>Object property values at the path (for maps keyed by id), or nothing.</summary>
    public static IEnumerable<JsonProperty> Obj(this JsonElement element, string path)
    {
        var value = element.Path(path);
        return value is { ValueKind: JsonValueKind.Object } obj ? obj.EnumerateObject() : [];
    }
}
