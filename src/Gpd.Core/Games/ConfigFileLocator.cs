// ============================================================================
//  Gpd.Core / Games / ConfigFileLocator.cs
//  ---------------------------------------------------------------------------
//  给一个 GameInfo 找出它的画质/设置配置文件。覆盖的真实分布：
//    · 安装目录内（含 Source 引擎的 cfg\、Unity 的 *_Data、UE 的 Engine\Config）
//    · UE：%LOCALAPPDATA%\<游戏>\Saved\Config\**\*.ini（含 GameUserSettings.ini）
//    · Unity：%USERPROFILE%\AppData\LocalLow\<公司>\<产品>\
//    · %USERPROFILE%\Documents\My Games\<游戏>\（如 Watch_Dogs 2 的 WD2_GamerProfile.xml）
//    · %USERPROFILE%\Saved Games\<发行商>\<游戏>\（如 Apex 的 settings.cfg / videoconfig.txt）
//    · %APPDATA%\<游戏>\
//  全程只读，并且对文件数量与单文件体积都设了上限。
// ============================================================================

namespace Gpd.Core.Games;

/// <summary>一个候选配置文件的来源优先级 + 用途。</summary>
public sealed record ConfigCandidate(string Path, string Purpose, int Priority);

/// <summary>用户目录里被名字规则命中的游戏目录。</summary>
public sealed record MatchedDirectory(string Path, string Source, int Priority);

/// <summary>画质/设置配置文件定位器。</summary>
public static class ConfigFileLocator
{
    /// <summary>每个游戏最多收集多少个配置文件。</summary>
    public const int MaxFilesPerGame = 40;

    /// <summary>单文件超过这个体积就不读内容（只记元数据）。</summary>
    public const long MaxFileSizeBytes = ConfigFileReader.DefaultMaxFileSizeBytes;

    /// <summary>安装目录里向下找配置文件的层数。</summary>
    private const int InstallDirSearchDepth = 3;

    /// <summary>用户目录里向下找配置文件的层数（UE 的 Saved\Config\WindowsNoEditor\ 需要 4 层）。</summary>
    private const int UserDirSearchDepth = 4;

    /// <summary>安装目录里最多枚举多少个文件，防止超大目录拖慢扫描。</summary>
    private const int InstallDirMaxFiles = 4000;

    private static readonly string[] CandidateExtensions =
    {
        ".ini", ".cfg", ".conf", ".json", ".xml", ".txt", ".properties", ".settings", ".config",
    };

    /// <summary>文件名里出现这些片段才算"设置文件"（对 .json/.txt 这类通用扩展名做额外约束）。</summary>
    private static readonly string[] SettingsNameFragments =
    {
        "config", "setting", "video", "graphic", "display", "gfx", "render", "option",
        "pref", "gameusersettings", "engine", "autoexec", "profile", "quality", "performance",
    };

    /// <summary>对所有已识别的游戏补齐 <see cref="GameInfo.ConfigFiles"/>。</summary>
    public static void Populate(GameInfo game, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(warnings);

        var candidates = FindCandidates(game);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedLarge = 0;

        foreach (var candidate in candidates
                     .OrderBy(c => c.Priority)
                     .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (game.ConfigFiles.Count >= MaxFilesPerGame)
            {
                break;
            }

            if (!seen.Add(candidate.Path))
            {
                continue;
            }

            if (!GameLibraryUtil.FileExists(candidate.Path))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(candidate.Path).Length;
            }
            catch (Exception)
            {
                size = 0;
            }

            if (size > MaxFileSizeBytes)
            {
                // 体积超限：仍然列出文件（让报告知道它存在），但明确标注未读取内容。
                game.ConfigFiles.Add(new ConfigFile
                {
                    Path = candidate.Path,
                    Format = ConfigFileReader.DetectFormat(candidate.Path),
                    Purpose = candidate.Purpose,
                    SizeBytes = size,
                    Error = $"文件 {size} 字节，超过 {MaxFileSizeBytes} 字节上限，未读取内容（只记录元数据）。",
                });
                skippedLarge++;
                continue;
            }

            game.ConfigFiles.Add(ConfigFileReader.Read(candidate.Path, candidate.Purpose, MaxFileSizeBytes));
        }

        if (skippedLarge > 0)
        {
            warnings.Add($"{game.Name}：有 {skippedLarge} 个体积超限的配置文件只记录了元数据，未解析内容。");
        }

        if (game.ConfigFiles.Count == 0)
        {
            warnings.Add($"{game.Name}：没有找到任何画质/设置配置文件（安装目录与常见用户目录都已检查）。");
        }
    }

    /// <summary>列出候选配置文件（已去重、已按可信度分级），不读取内容。</summary>
    public static List<ConfigCandidate> FindCandidates(GameInfo game)
    {
        ArgumentNullException.ThrowIfNull(game);

        var results = new List<ConfigCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CollectFromInstallDir(game).Concat(CollectFromUserDirectories(game)))
        {
            if (seen.Add(candidate.Path))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    /// <summary>
    /// 仅供自测/报告使用：列出用户目录里被"游戏名匹配规则"命中的目录。
    /// 即使目录里没有设置文件，也能据此判断路径规则是否生效。
    /// </summary>
    public static List<MatchedDirectory> FindMatchedUserDirectories(GameInfo game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return MatchedUserDirectories(game).ToList();
    }

    // ------------------------------------------------------------------ 安装目录
    private static IEnumerable<ConfigCandidate> CollectFromInstallDir(GameInfo game)
    {
        if (!GameLibraryUtil.DirectoryExists(game.InstallDir))
        {
            yield break;
        }

        // 只走一遍目录树（3A 游戏的安装目录动辄几万文件，逐个扩展名走一遍会明显变慢），
        // 边走边按扩展名与文件名过滤；超过上限就停。
        foreach (var file in GameLibraryUtil.SafeEnumerateFiles(
                     game.InstallDir, "*", InstallDirSearchDepth, InstallDirMaxFiles))
        {
            var extension = Path.GetExtension(file);
            if (!CandidateExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSettingsLikeFile(file))
            {
                continue;
            }

            yield return new ConfigCandidate(file, DerivePurpose(file, game), 1);
        }
    }

    // ------------------------------------------------------------------ 用户目录
    private static IEnumerable<ConfigCandidate> CollectFromUserDirectories(GameInfo game)
    {
        foreach (var (dir, _, priority) in MatchedUserDirectories(game))
        {
            foreach (var file in SafeEnumerateSettingsFiles(dir, UserDirSearchDepth))
            {
                yield return new ConfigCandidate(file, DerivePurpose(file, game), priority);
            }
        }
    }

    /// <summary>按名字规则在本机用户目录里找出属于这个游戏的目录。</summary>
    private static IEnumerable<MatchedDirectory> MatchedUserDirectories(GameInfo game)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // 1) UE：%LOCALAPPDATA%\<游戏>\
        foreach (var dir in SafeGetDirectories(localAppData, 1))
        {
            var leaf = Path.GetFileName(dir);
            if (MatchesGame(leaf, game))
            {
                yield return new MatchedDirectory(dir, $"UE LocalAppData（{leaf}）", 2);
            }
        }

        // 2) %USERPROFILE%\Documents\My Games\<游戏>\
        foreach (var dir in SafeGetDirectories(Path.Combine(documents, "My Games"), 2))
        {
            var leaf = Path.GetFileName(dir);
            var parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? string.Empty);
            if (MatchesGame(leaf, game) || MatchesGame(parent, game))
            {
                yield return new MatchedDirectory(dir, $"My Games（{parent}\\{leaf}）", 2);
            }
        }

        // 3) %USERPROFILE%\Saved Games\<发行商>\<游戏>\（Apex 就是 Saved Games\Respawn\Apex）
        foreach (var dir in SafeGetDirectories(Path.Combine(userProfile, "Saved Games"), 2))
        {
            var leaf = Path.GetFileName(dir);
            var parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? string.Empty);
            if (MatchesGame(leaf, game) || MatchesGame(parent, game))
            {
                yield return new MatchedDirectory(dir, $"Saved Games（{parent}\\{leaf}）", 2);
            }
        }

        // 4) Unity：%USERPROFILE%\AppData\LocalLow\<公司>\<产品>\
        foreach (var companyDir in SafeGetDirectories(Path.Combine(userProfile, "AppData", "LocalLow"), 1))
        {
            foreach (var productDir in SafeGetDirectories(companyDir, 1))
            {
                var product = Path.GetFileName(productDir);
                var company = Path.GetFileName(companyDir);
                if (MatchesGame(product, game) || MatchesGame(company, game))
                {
                    yield return new MatchedDirectory(productDir, $"LocalLow（{company}\\{product}）", 3);
                }
            }
        }

        // 5) %APPDATA%\<游戏>\
        foreach (var dir in SafeGetDirectories(appData, 1))
        {
            var leaf = Path.GetFileName(dir);
            if (MatchesGame(leaf, game))
            {
                yield return new MatchedDirectory(dir, $"Roaming AppData（{leaf}）", 3);
            }
        }
    }

    private static bool MatchesGame(string directoryName, GameInfo game)
        => GameLibraryUtil.LooksRelated(directoryName, game.Name)
           || GameLibraryUtil.LooksRelated(directoryName, game.ProcessName);

    // -------------------------------------------------------------------- 工具方法
    private static IEnumerable<string> SafeEnumerateSettingsFiles(string directory, int maxDepth)
    {
        foreach (var extension in new[] { ".ini", ".cfg", ".xml", ".json", ".txt", ".conf", ".properties" })
        {
            foreach (var file in GameLibraryUtil.SafeEnumerateFiles(directory, "*" + extension, maxDepth, 400))
            {
                if (IsSettingsLikeFile(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> SafeGetDirectories(string root, int maxDepth)
    {
        if (!GameLibraryUtil.DirectoryExists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;

                if (depth + 1 < maxDepth)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// 这些目录里装的是本地化文本、日志、缓存、着色器一类的数据，不是画质设置。
    /// 实测教训：OBS 的 <c>data\obs-studio\locale\*.ini</c>（1377 键的翻译文件）和
    /// MyDockFinder 的 <c>lang\*.ini</c>（871 键）会被当成"画质配置"，
    /// 从而产出 "EffectFilters = Parzûnên bandorê" 这种库尔德语假结论。
    /// 命中就整目录跳过（比较的是路径里的任何一段目录名）。
    /// </summary>
    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 本地化
        "locale", "locales", "localization", "localisation", "lang", "langs", "language", "languages",
        "i18n", "l10n", "translations", "translation", "resx",
        // 日志/崩溃/缓存
        "logs", "log", "crash", "crashes", "crashdumps", "dumps", "cache", "cached",
        // 美术资源（里面的 json/xml 是素材描述，不是画质档位）
        "sounds", "sound", "audio", "music", "movies", "videos", "video", "fonts", "font",
        "shaders", "shader", "textures", "texture", "materials", "models", "meshes", "animations",
        // 文档/许可/运行时
        "docs", "documentation", "licenses", "license", "redist", "redistributables", "directx",
        "vcredist", "dotnet", "node_modules", ".git", "obj", "screenshots",
    };

    /// <summary>形如 zh-CN / pt-BR / af-ZA / en_US 的语言代码文件名（这类文件是翻译表）。</summary>
    private static readonly System.Text.RegularExpressions.Regex LocaleCodePattern =
        new(@"^[a-z]{2,3}([-_][A-Za-z]{2,4}){1,2}$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>路径里是否有被排除的目录名。</summary>
    private static bool IsInSkippedDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        foreach (var segment in directory.Split('\\', '/'))
        {
            if (segment.Length > 0 && SkipDirectoryNames.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>文件名是否像"设置文件"：专门的配置扩展名，或名字里带 config/settings/video 等片段。</summary>
    private static bool IsSettingsLikeFile(string filePath)
    {
        if (IsInSkippedDirectory(filePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);

        // 语言包：lang\Arabic.ini 之外的 zh-CN.ini / locale.ini 这类也要挡掉。
        if (LocaleCodePattern.IsMatch(name)
            || name.StartsWith("locale", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("translation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SettingsNameFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".ini" or ".cfg" or ".conf" or ".properties" or ".settings";
    }

    /// <summary>根据文件名与所在位置推断用途（写进报告的"这是什么文件"）。</summary>
    private static string DerivePurpose(string filePath, GameInfo game)
    {
        var name = Path.GetFileName(filePath).ToLowerInvariant();
        var directory = (Path.GetDirectoryName(filePath) ?? string.Empty).ToLowerInvariant();

        if (name.Contains("gameusersettings"))
        {
            return "UE 引擎图形设置";
        }

        if (name.Contains("engine.ini") || name.Contains("scalability"))
        {
            return "UE 引擎/画质档位配置";
        }

        if (name.Contains("autoexec"))
        {
            return "启动脚本（可能包含画质指令）";
        }

        if (name.Contains("video") || name.Contains("graphic") || name.Contains("gfx")
            || name.Contains("display") || name.Contains("render"))
        {
            return "图形/画质设置";
        }

        if (name.Contains("gamerprofile") || name.Contains("profile"))
        {
            return "游戏档案（含画质相关设置）";
        }

        if (name.Contains("input") || name.Contains("keybind") || name.Contains("control"))
        {
            return "操作/按键设置";
        }

        if (name.Contains("audio") || name.Contains("sound"))
        {
            return "音频设置";
        }

        if (name.Contains("settings") || name.Contains("config") || name.Contains("option") || name.Contains("pref"))
        {
            return "游戏设置";
        }

        if (directory.Contains("saved\\config") || directory.Contains("saved/config"))
        {
            return "UE Saved 配置目录";
        }

        if (directory.Contains(@"\cfg") || directory.EndsWith("cfg", StringComparison.Ordinal))
        {
            return "引擎 cfg 目录";
        }

        return $"配置文件（{game.Name}）";
    }
}
