// ============================================================================
//  Gpd.Core / Games / GraphicsSettingsAnalyzer.cs
//  ---------------------------------------------------------------------------
//  把配置文件里的原始键值翻译成"这意味着什么"：设置名 / 归一化档位 / 性能影响 / 建议。
//  只做启发式识别，认不出来的键就不产出结论（不会瞎猜）。
//  覆盖 UE 的 sg.* 画质档位与 GameUserSettings.ini、Source 引擎的 r_* 指令、
//  以及常见的分辨率 / 垂直同步 / 帧率上限 / 超分(DLSS/FSR) 等写法。
// ============================================================================

namespace Gpd.Core.Games;

/// <summary>画质配置解读器。</summary>
public static class GraphicsSettingsAnalyzer
{
    /// <summary>一条识别规则。</summary>
    private sealed record Rule(
        string[] Fragments,
        string SettingName,
        int Impact,
        Func<string, string> Normalize,
        string Comment);

    // 说明：Fragments 里的短片段（<4 字符）要求"整键相等"才命中，避免 fov/aa 之类误伤。
    private static readonly Rule[] Rules =
    {
        // ---------------------------------------------------------- 画质档位（UE sg.*）
        new(new[] { "sg.resolutionquality" }, "分辨率缩放质量", 4, NormalizePercent,
            "UE 的屏幕分辨率百分比，直接决定 GPU 负载；调低是最有效的提帧手段之一。"),
        new(new[] { "sg.viewdistancequality" }, "视野距离", 2, NormalizeUeQuality,
            "影响远景物体与植被的绘制距离，主要吃 CPU 与显存。"),
        new(new[] { "sg.antialiasingquality" }, "抗锯齿", 3, NormalizeUeQuality,
            "TAA/MSAA 之类的抗锯齿，档位越高 GPU 开销越大。"),
        new(new[] { "sg.shadowquality" }, "阴影质量", 3, NormalizeUeQuality,
            "阴影分辨率与级联数量，是常见的 GPU 瓶颈项。"),
        new(new[] { "sg.postprocessquality" }, "后处理质量", 3, NormalizeUeQuality,
            "泛光/景深/色调映射等后处理，越高越吃 GPU。"),
        new(new[] { "sg.texturequality" }, "纹理质量", 2, NormalizeUeQuality,
            "主要吃显存；显存不足时会明显掉帧。"),
        new(new[] { "sg.effectsquality" }, "特效质量", 3, NormalizeUeQuality,
            "粒子与特效复杂度，战斗中影响明显。"),
        new(new[] { "sg.foliagequality" }, "植被质量", 2, NormalizeUeQuality,
            "植被密度与摆动，开阔场景影响较大。"),
        new(new[] { "sg.shadingquality" }, "着色质量", 3, NormalizeUeQuality,
            "材质着色复杂度，越高越吃 GPU。"),
        new(new[] { "scalability" }, "画质档位组", 3, NormalizeUeQuality,
            "UE 的画质档位总控（0=低 … 4=影视级）。"),

        // ---------------------------------------------------------- 分辨率
        new(new[] { "screenpercentage", "resolutionquality" }, "渲染分辨率百分比", 4, NormalizePercent,
            "低于 100% 表示内部按更低分辨率渲染再放大；调低提帧最明显，但画面会变糊。"),
        // 顺序有讲究：带 height 的规则必须排在裸 "defaultres" 之前，
        // 否则 setting.defaultresheight 会被 "defaultres" 片段抢先命中，标成"分辨率宽度"。
        new(new[] { "defaultreswidth", "resolutionwidth", "reswidth", "screenwidth" }, "分辨率宽度", 3, NormalizeRaw,
            "与高度一起决定像素总量，是 GPU 负载的第一大项。"),
        new(new[] { "defaultresheight", "resolutionheight", "resheight", "screenheight" }, "分辨率高度", 3, NormalizeRaw,
            "与宽度一起决定像素总量。"),
        new(new[] { "defaultres" }, "分辨率宽度", 3, NormalizeRaw,
            "与高度一起决定像素总量，是 GPU 负载的第一大项。"),

        // ---------------------------------------------------------- 显示模式 / 同步
        new(new[] { "fullscreenmode", "windowmode", "fullscreen" }, "显示模式", 1, NormalizeFullscreen,
            "独占全屏通常比无边框窗口延迟更低、帧率更稳。"),
        new(new[] { "vsync", "waitforvsync" }, "垂直同步", 1, NormalizeToggle,
            "开启会把帧率锁在刷新率并增加输入延迟；追求响应速度建议关闭，配合可变刷新率(VRR)。"),
        new(new[] { "frameratelinemaxfps", "frameratelimit", "maxfps", "fpsmax", "fps_max", "targetfps", "fpscap" },
            "帧率上限", 1, NormalizeRaw,
            "限制最大帧率可以降低功耗与发热；如果明显低于显示器刷新率会感觉不够流畅。"),

        // ---------------------------------------------------------- 超分 / 光追
        new(new[] { "dlss", "dlaa" }, "DLSS（NVIDIA 超分）", 0, NormalizeUpscaler,
            "用 AI 放大提高帧率；质量档位越高越清晰、收益越小。"),
        new(new[] { "fsr", "fidelityfx" }, "FSR（AMD 超分）", 0, NormalizeUpscaler,
            "跨厂商的空间/时域放大，能明显提帧。"),
        new(new[] { "xess", "xessquality" }, "XeSS（Intel 超分）", 0, NormalizeUpscaler,
            "Intel 超分方案，提帧同时尽量保留细节。"),
        new(new[] { "raytracing", "ray_tracing", "rtxgi", "dXR", "dxr", "globalillumination", "pathtracing" },
            "光线追踪", 5, NormalizeToggle,
            "光追是当前最吃性能的选项，通常需要配合超分使用。"),
        new(new[] { "reflex", "lowlatency", "nvidia_reflex" }, "NVIDIA Reflex 低延迟", 0, NormalizeToggle,
            "降低渲染队列延迟，几乎不影响画质，建议开启。"),
        new(new[] { "hdr" }, "HDR", 1, NormalizeToggle,
            "高动态范围显示，对性能影响不大但需要显示器支持。"),

        // ---------------------------------------------------------- 具体效果
        new(new[] { "motionblur" }, "动态模糊", 1, NormalizeToggle,
            "纯观感选项，关闭可以提升画面清晰度。"),
        new(new[] { "bloom" }, "泛光", 1, NormalizeToggle, "光晕效果，性能影响较小。"),
        new(new[] { "depthoffield" }, "景深", 2, NormalizeToggle, "后处理中的景深，关闭可略微提帧并让画面更锐利。"),
        new(new[] { "ambientocclusion", "ssao", "hbao", "gtao", "ssao" }, "环境光遮蔽", 3, NormalizeToggle,
            "让接触阴影更真实，中等 GPU 开销。"),
        new(new[] { "anisotropic" }, "各向异性过滤", 1, NormalizeAniso,
            "对性能影响极小，建议保持 8x/16x。"),
        new(new[] { "texture" }, "纹理质量", 2, NormalizeUeQuality, "主要吃显存。"),
        new(new[] { "shadow" }, "阴影质量", 3, NormalizeUeQuality, "阴影分辨率和距离，常见 GPU 瓶颈。"),
        new(new[] { "anti-aliasing", "antialiasing", "msaa", "taa", "fxaa" }, "抗锯齿", 3, NormalizeUeQuality,
            "抗锯齿档位或方式，越高越平滑也越吃 GPU。"),
        new(new[] { "volumetric", "volumetricfog" }, "体积雾", 3, NormalizeToggle, "体积光/雾，特定场景下开销明显。"),
        new(new[] { "foliage", "grass" }, "植被", 2, NormalizeUeQuality, "植被密度，开阔场景影响较大。"),
        new(new[] { "tessellation" }, "曲面细分", 3, NormalizeToggle, "细分几何，GPU 开销中等。"),
        new(new[] { "effect", "particle" }, "特效/粒子", 3, NormalizeUeQuality, "战斗特效多时影响明显。"),
        new(new[] { "viewdistance", "drawdistance", "lod" }, "视野/绘制距离", 2, NormalizeUeQuality,
            "远景绘制距离，吃 CPU 与显存。"),
        new(new[] { "fov" }, "视野角度(FOV)", 1, NormalizeRaw,
            "视野越大画面越广，也会略微增加渲染负载。"),
        new(new[] { "gamma", "brightness" }, "亮度/Gamma", 0, NormalizeRaw, "纯显示调整，不影响性能。"),
        new(new[] { "mousesensitivity", "sensitivity" }, "鼠标灵敏度", 0, NormalizeRaw, "操作设置，不影响性能。"),
    };

    /// <summary>识别单个配置文件里的画质设置。</summary>
    public static List<GraphicsSettingInsight> Analyze(ConfigFile configFile)
    {
        ArgumentNullException.ThrowIfNull(configFile);
        return Analyze(configFile.Entries);
    }

    /// <summary>识别一个游戏全部配置文件里的画质设置（按设置名去重，保留信息量更大的那条）。</summary>
    public static List<GraphicsSettingInsight> Analyze(GameInfo game)
    {
        ArgumentNullException.ThrowIfNull(game);

        var all = new List<GraphicsSettingInsight>();
        foreach (var file in game.ConfigFiles)
        {
            all.AddRange(Analyze(file.Entries));
        }

        return MergeBySettingName(all);
    }

    /// <summary>
    /// 同一设置名只保留一条：已经是明确档位的（NormalizedValue 与原值不同）不被后面的
    /// "原始值"覆盖，避免安装目录里的 cfg 与存档目录里的 cfg 把同一项刷屏。
    /// </summary>
    private static List<GraphicsSettingInsight> MergeBySettingName(List<GraphicsSettingInsight> insights)
    {
        var merged = new Dictionary<string, GraphicsSettingInsight>(StringComparer.OrdinalIgnoreCase);
        foreach (var insight in insights)
        {
            if (merged.TryGetValue(insight.SettingName, out var existing))
            {
                if (existing.NormalizedValue != existing.RawValue)
                {
                    continue;
                }
            }

            merged[insight.SettingName] = insight;
        }

        return merged.Values.OrderByDescending(i => i.PerformanceImpact).ToList();
    }

    /// <summary>识别一组键值里的画质设置。</summary>
    public static List<GraphicsSettingInsight> Analyze(IEnumerable<ConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries as IList<ConfigEntry> ?? entries.ToList();
        var lookup = new Dictionary<string, ConfigEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            var key = entry.Key.Trim();
            if (key.Length == 0 || key.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            var full = entry.Section.Length == 0 ? key : entry.Section + "/" + key;
            lookup[full] = entry;
            if (!lookup.ContainsKey(key))
            {
                lookup[key] = entry;
            }
        }

        var results = new List<GraphicsSettingInsight>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 分辨率要横纵一起看，先合成一条。
        var width = FindFirst(lookup, "resolutionsizex", "resolutionwidth", "defaultreswidth", "defaultres",
            "reswidth", "screenwidth");
        var height = FindFirst(lookup, "resolutionsizey", "resolutionheight", "defaultresheight", "resheight",
            "screenheight");
        if (width is not null && height is not null)
        {
            usedKeys.Add(width.Key);
            usedKeys.Add(height.Key);
            results.Add(new GraphicsSettingInsight
            {
                SettingName = "分辨率",
                SourceKey = width.Section.Length == 0 ? width.Key : width.Section + "/" + width.Key,
                RawValue = $"{width.Value} × {height.Value}",
                NormalizedValue = $"{width.Value} × {height.Value}",
                PerformanceImpact = 4,
                Comment = "像素总量决定 GPU 基础负载；降低分辨率或配合超分是提帧最直接的办法。",
            });
        }

        foreach (var entry in list)
        {
            var key = entry.Key.Trim();
            if (key.Length == 0 || key.StartsWith("__", StringComparison.Ordinal) || usedKeys.Contains(key))
            {
                continue;
            }

            var rule = MatchRule(key);
            if (rule is null || !IsPlausibleValue(entry.Value))
            {
                continue;
            }

            usedKeys.Add(key);
            results.Add(new GraphicsSettingInsight
            {
                SettingName = rule.SettingName,
                SourceKey = entry.Section.Length == 0 ? key : entry.Section + "/" + key,
                RawValue = entry.Value,
                NormalizedValue = SafeNormalize(rule, entry.Value),
                PerformanceImpact = rule.Impact,
                Comment = rule.Comment,
            });
        }

        return MergeBySettingName(results);
    }

    private static ConfigEntry? FindFirst(Dictionary<string, ConfigEntry> lookup, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (lookup.TryGetValue(key, out var entry))
            {
                return entry;
            }

            // 也接受带节名前缀/带 r_ 前缀的写法。
            foreach (var pair in lookup)
            {
                var tail = pair.Key;
                var slash = tail.LastIndexOf('/');
                if (slash >= 0)
                {
                    tail = tail[(slash + 1)..];
                }

                if (tail.Equals(key, StringComparison.OrdinalIgnoreCase)
                    || tail.Equals("r_" + key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 这个值像不像"一个设置项的值"。用来挡掉本地化文本造成的假结论。
    /// 实测踩过的坑：OBS 库尔德语语言包里 <c>Basic.Filters.EffectFilters = Parzûnên bandorê</c>、
    /// MyDockFinder 越南语语言包里 <c>editicon/iconshadow = Bong bóng biểu tượng</c>，
    /// 以及 CS2 里 <c>"particle_asset", = (空)</c> 这种空值条目。
    /// </summary>
    private static bool IsPlausibleValue(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length == 0 || text.Length > 64)
        {
            return false;
        }

        // 翻译模板占位符：%1 / %s / {0}
        if (text.Contains("%1", StringComparison.Ordinal) || text.Contains("%s", StringComparison.Ordinal)
            || text.Contains("%d", StringComparison.Ordinal)
            || (text.Contains('{') && text.Contains('}')))
        {
            return false;
        }

        // 带空格的句子。两词的英文档位（Very High / Windowed Fullscreen）要放行，
        // 所以只在"3 个词以上"或"含非 ASCII 字母"时判为本地化文本。
        if (text.Contains(' '))
        {
            if (text.Count(c => c == ' ') >= 2)
            {
                return false;
            }

            if (text.Any(c => c > 127 && char.IsLetter(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static Rule? MatchRule(string key)
    {
        var lower = key.ToLowerInvariant();
        // 去掉节名前缀，只按最后一段键名匹配，避免父节点名误命中。
        var slash = lower.LastIndexOf('/');
        var tail = slash >= 0 ? lower[(slash + 1)..] : lower;

        // JSON 数组下标键（passes[0]/textures[0]）是素材数组元素，不是命名设置项。
        if (tail.Contains('[') || tail.Contains(']'))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            foreach (var fragment in rule.Fragments)
            {
                // 短片段（例如 "fov"、"lod"）必须是整键相等，否则 "fovea"/"payload" 都会误命中。
                var matched = fragment.Length < 4
                    ? tail.Equals(fragment, StringComparison.Ordinal) || tail.Equals("r_" + fragment, StringComparison.Ordinal)
                    : tail.Contains(fragment, StringComparison.Ordinal);
                if (matched)
                {
                    return rule;
                }
            }
        }

        return null;
    }

    private static string SafeNormalize(Rule rule, string rawValue)
    {
        try
        {
            var normalized = rule.Normalize(rawValue);
            return normalized.Length == 0 ? rawValue : normalized;
        }
        catch (Exception)
        {
            return rawValue;
        }
    }

    // ---------------------------------------------------------------- 归一化函数
    private static string NormalizeRaw(string value) => value.Trim();

    private static string NormalizeToggle(string value)
    {
        var text = value.Trim().Trim('"').ToLowerInvariant();
        return text switch
        {
            "1" or "true" or "on" or "yes" or "enabled" => "开启",
            "0" or "false" or "off" or "no" or "disabled" => "关闭",
            _ => value.Trim(),
        };
    }

    private static string NormalizeUeQuality(string value)
        => value.Trim() switch
        {
            "0" => "低",
            "1" => "中",
            "2" => "高",
            "3" => "极高",
            "4" => "影视级",
            _ => NormalizeToggle(value),
        };

    private static string NormalizePercent(string value)
    {
        var number = IniConfigParser.ParseNumber(value);
        if (number is null)
        {
            return value.Trim();
        }

        var percent = number.Value <= 2.0 ? number.Value * 100 : number.Value;
        return $"{Math.Round(percent, 1)}%";
    }

    private static string NormalizeFullscreen(string value)
    {
        var text = value.Trim().Trim('"').ToLowerInvariant();
        return text switch
        {
            // UE 约定：0=独占全屏，1=无边框窗口，2=窗口化。
            "0" or "fullscreen" or "exclusivefullscreen" => "独占全屏",
            "1" or "windowedfullscreen" or "borderless" => "无边框窗口",
            "2" or "windowed" => "窗口化",
            _ => value.Trim(),
        };
    }

    private static string NormalizeAniso(string value)
    {
        var text = value.Trim().Trim('"').ToLowerInvariant();
        return text switch
        {
            "0" or "off" or "false" => "关闭",
            "1" or "2" or "4" or "8" or "16" => text + "x",
            _ => value.Trim(),
        };
    }

    private static string NormalizeUpscaler(string value)
    {
        var text = value.Trim().Trim('"');
        return text.ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "disabled" => "关闭",
            "1" or "on" or "true" or "enabled" => "开启",
            "ultraperformance" => "超高性能",
            "performance" => "性能",
            "balanced" => "均衡",
            "quality" => "质量",
            "ultraquality" or "dlaa" => "超高画质",
            _ => text,
        };
    }
}
