// ============================================================================
//  Gpd.Core / Games / JsonConfigParser.cs
//  ---------------------------------------------------------------------------
//  JSON 配置解析器：把嵌套结构压平成 节名/键名/值 三元组，方便报告逐条展示。
//  只读；解析失败只写 Error，不抛异常。
// ============================================================================

using System.Text.Json;

namespace Gpd.Core.Games;

/// <summary>JSON 解析器。</summary>
public sealed class JsonConfigParser : IConfigParser
{
    /// <summary>最多产出多少条键值。</summary>
    public const int MaxEntries = 20000;

    /// <summary>最多压平多少层；再深就只记一条提示。</summary>
    public const int MaxDepth = 8;

    /// <summary>数组最多展开多少项。</summary>
    public const int MaxArrayItems = 200;

    public bool CanParse(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase);

    public ConfigFile Parse(string filePath)
    {
        var result = new ConfigFile
        {
            Path = filePath,
            Format = "json",
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
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 64,
            };

            using var document = JsonDocument.Parse(text, options);
            Flatten(document.RootElement, string.Empty, string.Empty, 0, result.Entries);
        }
        catch (JsonException ex)
        {
            result.Error = $"JSON 语法错误：{ex.Message}";
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

    private static void Flatten(JsonElement element, string section, string key, int depth, List<ConfigEntry> sink)
    {
        if (sink.Count >= MaxEntries)
        {
            return;
        }

        if (depth > MaxDepth)
        {
            sink.Add(new ConfigEntry
            {
                Section = section,
                Key = key,
                Value = "__depth_limit__",
            });
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var childSection = key.Length == 0 ? section : Join(section, key);
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, childSection, property.Name, depth + 1, sink);
                    if (sink.Count >= MaxEntries)
                    {
                        return;
                    }
                }

                break;
            }

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index >= MaxArrayItems)
                    {
                        sink.Add(new ConfigEntry
                        {
                            Section = section,
                            Key = Join(key, "__array_truncated__"),
                            Value = $"数组超过 {MaxArrayItems} 项，已截断。",
                        });
                        break;
                    }

                    Flatten(item, section, $"{key}[{index}]", depth + 1, sink);
                    if (sink.Count >= MaxEntries)
                    {
                        return;
                    }

                    index++;
                }

                break;
            }

            default:
            {
                sink.Add(new ConfigEntry
                {
                    Section = section,
                    Key = key,
                    Value = ScalarToString(element),
                });
                break;
            }
        }
    }

    private static string ScalarToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => element.GetRawText(),
    };

    private static string Join(string section, string key)
        => section.Length == 0 ? key : section + "." + key;
}
