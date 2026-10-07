// ============================================================================
//  Gpd.Core / Games / ConfigFileReader.cs
//  ---------------------------------------------------------------------------
//  配置文件只读读取的统一入口：体积保护 + 元数据 + 解析器分发。
//  **任何情况下都只以 FileAccess.Read 打开文件，绝不写入或改动游戏文件。**
// ============================================================================

using System.Text;

namespace Gpd.Core.Games;

/// <summary>配置文件读取工具。</summary>
public static class ConfigFileReader
{
    /// <summary>超过这个体积的文件不读内容（只记录元数据），避免把几 MB 的日志/数据库读进内存。</summary>
    public const long DefaultMaxFileSizeBytes = 8 * 1024 * 1024;

    private static readonly IConfigParser[] Parsers =
    {
        new IniConfigParser(),
        new JsonConfigParser(),
        new XmlConfigParser(),
    };

    /// <summary>全部内置解析器。</summary>
    public static IReadOnlyList<IConfigParser> AllParsers => Parsers;

    /// <summary>按扩展名挑一个解析器；没有匹配返回 null（此时只记录文件元数据）。</summary>
    public static IConfigParser? SelectParser(string filePath)
        => Parsers.FirstOrDefault(p => p.CanParse(filePath));

    /// <summary>按扩展名给出格式名，与 <see cref="ConfigFile.Format"/> 的取值约定一致。</summary>
    public static string DetectFormat(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".ini" => "ini",
            ".cfg" => "cfg",
            ".conf" => "ini",
            ".json" => "json",
            ".xml" => "xml",
            ".config" => "xml",
            ".txt" => "txt",
            ".properties" => "ini",
            _ => "binary",
        };
    }

    /// <summary>只读读取全部文本；失败时给出中文原因，不抛异常。</summary>
    public static bool TryReadAllText(string filePath, long maxBytes, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                error = "文件不存在。";
                return false;
            }

            if (info.Length > maxBytes)
            {
                error = $"文件 {info.Length} 字节，超过 {maxBytes} 字节上限，未读取内容（只记录元数据）。";
                return false;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有读取权限（可能需要管理员身份）。";
            return false;
        }
        catch (IOException ex)
        {
            error = $"读取失败：{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"读取失败：{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>读取并解析一个配置文件，产出填好元数据的 <see cref="ConfigFile"/>。</summary>
    public static ConfigFile Read(string filePath, string purpose, long maxBytes = DefaultMaxFileSizeBytes)
    {
        var result = new ConfigFile
        {
            Path = filePath,
            Format = DetectFormat(filePath),
            Purpose = purpose,
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
            // 元数据拿不到不影响后续解析。
        }

        var parser = SelectParser(filePath);
        if (parser is null)
        {
            result.Error = $"没有可用于 {result.Format} 格式的解析器，仅记录文件信息。";
            return result;
        }

        try
        {
            var parsed = parser.Parse(filePath);
            result.Entries = parsed.Entries;
            result.Error = parsed.Error;
            if (parsed.SizeBytes > 0)
            {
                result.SizeBytes = parsed.SizeBytes;
            }

            if (parsed.LastWriteTime is not null)
            {
                result.LastWriteTime = parsed.LastWriteTime;
            }
        }
        catch (Exception ex)
        {
            result.Error = $"解析异常：{ex.GetType().Name} {ex.Message}";
        }

        return result;
    }

    /// <summary>给条目列表补一条"已截断"的提示项，便于报告里如实说明。</summary>
    internal static void AppendTruncationNotice(List<ConfigEntry> entries, string reason)
        => entries.Add(new ConfigEntry { Section = "__meta__", Key = "__truncated__", Value = reason });
}
