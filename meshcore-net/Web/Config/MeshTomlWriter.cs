using System.Globalization;
using System.Text;

namespace MeshCoreNet;

public static class MeshTomlWriter
{
    public static string Write(Dictionary<string, object?> config)
    {
        var builder = new StringBuilder();
        WriteSection(builder, config, Array.Empty<string>());
        return builder.ToString();
    }

    private static void WriteSection(StringBuilder builder, Dictionary<string, object?> section, IReadOnlyList<string> prefix)
    {
        var scalarKeys = section.Keys.Where(key => section[key] is not Dictionary<string, object?>).OrderBy(key => key).ToList();
        foreach (var key in scalarKeys)
        {
            if (section[key] is Dictionary<string, object?>)
            {
                continue;
            }

            builder.Append(key);
            builder.Append(" = ");
            AppendValue(builder, section[key]);
            builder.AppendLine();
        }

        foreach (var nested in section.Where(kvp => kvp.Value is Dictionary<string, object?>).OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sectionPath = prefix.Append(nested.Key).ToArray();
            builder.AppendLine();
            builder.Append('[');
            builder.Append(string.Join('.', sectionPath));
            builder.AppendLine("]");
            WriteSection(builder, (Dictionary<string, object?>)nested.Value!, sectionPath);
        }
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool boolValue:
                builder.Append(boolValue ? "true" : "false");
                break;
            case int or long or double:
                builder.AppendConvertInvariant(value);
                break;
            case string stringValue:
                builder.Append('"');
                builder.Append(stringValue.Replace("\"", "\\\""));
                builder.Append('"');
                break;
            case List<object?> listValue:
                builder.Append('[');
                builder.Append(string.Join(", ", listValue.Select(FormatValue)));
                builder.Append(']');
                break;
            default:
                builder.Append('"');
                builder.Append(value.ToString()?.Replace("\"", "\\\"") ?? string.Empty);
                builder.Append('"');
                break;
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool boolValue => boolValue ? "true" : "false",
            int or long or double => value.ToString() ?? string.Empty,
            string stringValue => '"' + stringValue.Replace("\"", "\\\"") + '"',
            _ => '"' + value.ToString()?.Replace("\"", "\\\"") + '"'
        };
    }

    private static void AppendConvertInvariant(this StringBuilder builder, object value)
    {
        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
