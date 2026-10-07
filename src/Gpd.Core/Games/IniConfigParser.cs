// ============================================================================
//  Gpd.Core / Games / IniConfigParser.cs
//  ---------------------------------------------------------------------------
//  INI / CFG 解析器。除了标准的 key=value，还必须兼容两种真实存在的写法：
//    1) Source 引擎（CS2 / Apex 的 cfg）：以空白分隔的命令，例如  r_drawtracers "1"
//    2) KeyValues 风格：  "key"    "value"
//  只读；解析失败只写 Error，不抛异常。
// ============================================================================

using System.Globalization;

namespace Gpd.Core.Games;

/// <summary>INI / CFG 解析器。</summary>
public sealed class IniConfigParser : IConfigParser
{
    /// <summary>单个文件最多产出多少条键值，防止异常文件把内存撑爆。</summary>
    public const int MaxEntries = 20000;

    private static readonly string[] Extensions =
    {
        ".ini", ".cfg", ".conf", ".txt", ".properties", ".settings", ".vdf", ".default",
    };

    public bool CanParse(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public ConfigFile Parse(string filePath)
    {
        var result = new ConfigFile
        {
            Path = filePath,
            Format = ConfigFileReader.DetectFormat(filePath),
        };

        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
            {
                result.SizeBytes = info.Length;
                result.LastWriteTime = new DateTimeOffset(info.LastWriteTime);
            }
        }
        catch (Exception)
        {
            // 元数据取不到不影响解析。
        }

        if (!ConfigFileReader.TryReadAllText(filePath, ConfigFileReader.DefaultMaxFileSizeBytes, out var text, out var error))
        {
            result.Error = error;
            return result;
        }

        var section = string.Empty;
        var truncated = false;

        try
        {
            var lineNumber = 0;
            foreach (var rawLine in SplitLines(text))
            {
                lineNumber++;
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // 整行注释。
                if (line[0] is ';' or '#' || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                // 节名 [Section]。
                if (line[0] == '[')
                {
                    var close = line.IndexOf(']');
                    section = close > 1 ? line[1..close].Trim() : line[1..].Trim();
                    continue;
                }

                if (result.Entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }

                if (!TrySplitEntry(line, out var key, out var value))
                {
                    continue;
                }

                result.Entries.Add(new ConfigEntry
                {
                    Section = section,
                    Key = key,
                    Value = value,
                });
            }
        }
        catch (Exception ex)
        {
            result.Error = $"解析失败（第 {result.Entries.Count} 条后中断）：{ex.GetType().Name} {ex.Message}";
        }

        if (truncated)
        {
            ConfigFileReader.AppendTruncationNotice(
                result.Entries,
                $"文件条目超过 {MaxEntries} 条，已截断，仅保留前 {MaxEntries} 条。");
        }

        return result;
    }

    /// <summary>把一个非空、非注释行拆成键值。三种写法都支持。</summary>
    private static bool TrySplitEntry(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        // 写法 1：key=value / key:value（取第一个分隔符，值里可以继续有 = 或 :）
        var equalsIndex = line.IndexOf('=');
        var colonIndex = line.IndexOf(':');

        var separator = -1;
        if (equalsIndex >= 0 && (colonIndex < 0 || equalsIndex < colonIndex))
        {
            separator = equalsIndex;
        }
        else if (colonIndex > 0)
        {
            // 避免把 "C:\path" 这种值里的冒号当成键值分隔。
            var beforeColon = line[..colonIndex];
            if (beforeColon.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '"' or '\''))
            {
                separator = colonIndex;
            }
        }

        if (separator > 0)
        {
            key = Clean(line[..separator]);
            value = CleanValue(line[(separator + 1)..]);
            return key.Length > 0;
        }

        // 写法 2/3：空白分隔（Source 引擎 cfg、KeyValues）。
        var spaceIndex = IndexOfWhitespace(line);
        if (spaceIndex < 0)
        {
            // 孤立的裸命令（例如 quit），保留为无值条目。
            key = Clean(line);
            return key.Length > 0;
        }

        key = Clean(line[..spaceIndex]);
        value = CleanValue(line[spaceIndex..]);
        return key.Length > 0;
    }

    private static int IndexOfWhitespace(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>键名：去空白与包裹引号。</summary>
    private static string Clean(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Trim();
    }

    /// <summary>
    /// 值：只有当整段被"一对"引号包住时才去掉引号。
    /// 例如 <c>"a" "b"</c> 不能被拆成 <c>a" "b</c>，所以内部还有引号时保持原样。
    /// </summary>
    private static string CleanValue(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && !text[1..^1].Contains('"'))
        {
            text = text[1..^1];
        }

        return text.Trim();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>把字符串按不变文化解析成 double（供画质分析使用），失败返回 null。</summary>
    internal static double? ParseNumber(string value)
    {
        var text = value.Trim().Trim('"');
        if (text.Length == 0)
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        return null;
    }
}
