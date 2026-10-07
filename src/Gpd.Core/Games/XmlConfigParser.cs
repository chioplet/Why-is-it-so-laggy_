// ============================================================================
//  Gpd.Core / Games / XmlConfigParser.cs
//  ---------------------------------------------------------------------------
//  XML 配置解析器：把元素树压平成 节名(父元素路径)/键名(元素名)/值(文本或属性)。
//  为了安全，禁用 DTD 处理与外部实体解析（绝不让配置文件触发网络/文件访问）。
//  只读；解析失败只写 Error，不抛异常。
// ============================================================================

using System.Xml;
using System.Xml.Linq;

namespace Gpd.Core.Games;

/// <summary>XML 解析器。</summary>
public sealed class XmlConfigParser : IConfigParser
{
    /// <summary>最多产出多少条键值。</summary>
    public const int MaxEntries = 20000;

    private static readonly string[] Extensions = { ".xml", ".config", ".settings" };

    public bool CanParse(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Watch_Dogs 2 这类游戏把配置叫 WD2_GamerProfile.xml —— 扩展名已覆盖，
        // 这里再兜一层"名字里带 profile/settings 的无扩展名文件"。
        var name = Path.GetFileName(filePath);
        return extension.Length == 0
               && (name.Contains("profile", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("settings", StringComparison.OrdinalIgnoreCase));
    }

    public ConfigFile Parse(string filePath)
    {
        var result = new ConfigFile
        {
            Path = filePath,
            Format = "xml",
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

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };

            using var stringReader = new StringReader(text);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader);
            if (document.Root is not null)
            {
                foreach (var child in document.Root.Elements())
                {
                    Walk(child, string.Empty, result.Entries);
                }
            }
        }
        catch (XmlException ex)
        {
            result.Error = $"XML 语法错误（第 {ex.LineNumber} 行）：{ex.Message}";
            return result;
        }
        catch (Exception ex)
        {
            result.Error = $"解析失败：{ex.GetType().Name} {ex.Message}";
            return result;
        }

        if (result.Entries.Count >= MaxEntries)
        {
            ConfigFileReader.AppendTruncationNotice(
                result.Entries,
                $"条目超过 {MaxEntries} 条，已截断，仅保留前 {MaxEntries} 条。");
        }

        return result;
    }

    private static void Walk(XElement element, string parentPath, List<ConfigEntry> sink)
    {
        if (sink.Count >= MaxEntries)
        {
            return;
        }

        var path = parentPath.Length == 0 ? element.Name.LocalName : parentPath + "/" + element.Name.LocalName;

        foreach (var attribute in element.Attributes())
        {
            if (sink.Count >= MaxEntries)
            {
                return;
            }

            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            sink.Add(new ConfigEntry
            {
                Section = path,
                Key = "@" + attribute.Name.LocalName,
                Value = attribute.Value,
            });
        }

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            var text = (element.Value ?? string.Empty).Trim();

            // 自闭合元素 <Resolution Width="1920" Height="1080" />：属性已经把它描述完了，
            // 不能再补一条空文本条目（实测会多出 "[Video] Resolution = " 这种噪声）。
            if (text.Length > 0 || !element.HasAttributes)
            {
                sink.Add(new ConfigEntry
                {
                    Section = parentPath,
                    Key = element.Name.LocalName,
                    Value = text,
                });
            }

            return;
        }

        foreach (var child in children)
        {
            Walk(child, path, sink);
            if (sink.Count >= MaxEntries)
            {
                return;
            }
        }
    }
}
