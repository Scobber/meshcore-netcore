using System.Text;
using System.Text.Json;

namespace MeshCoreNet;

public sealed partial class MeshWebServer
{
    private Dictionary<string, object?>? GetSection(params string[] path)
    {
        object? current = _config;
        foreach (var segment in path)
        {
            if (current is not Dictionary<string, object?> dictionary)
            {
                return null;
            }

            if (!dictionary.TryGetValue(segment, out current) || current is null)
            {
                return null;
            }
        }

        return current as Dictionary<string, object?>;
    }

    private static string? GetString(Dictionary<string, object?> section, string key)
    {
        if (!section.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value.ToString();
    }

    private void SetSection(string sectionName, string key, Dictionary<string, object?> section)
    {
        if (_config.TryGetValue(sectionName, out var existing) && existing is Dictionary<string, object?> existingSection)
        {
            existingSection[key] = section;
            return;
        }

        _config[sectionName] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = section
        };
    }

    private async Task SaveConfigAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(_configPath, MeshTomlWriter.Write(_config), Encoding.UTF8, cancellationToken);
    }

    private static ISet<string> GetStringSet(Dictionary<string, object?> section, params string[] keys)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!section.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is List<object?> values)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                    {
                        result.Add(item.ToString()!);
                    }
                }
            }
            else
            {
                result.Add(value.ToString()!);
            }
        }

        return result;
    }

    private static int GetInt(Dictionary<string, object?>? section, string key, int defaultValue)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}
