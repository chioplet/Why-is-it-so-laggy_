// ============================================================================
//  Gpd.Core / Games / GameLibraryUtil.cs
//  ---------------------------------------------------------------------------
//  游戏库识别共用的底层工具：路径归一化、注册表卸载项枚举、主可执行文件挑选。
//  全部只读；任何 IO/注册表异常都吞掉并写进 notes/warnings，绝不向外抛。
// ============================================================================

using System.Security;
using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>注册表卸载项（Uninstall 键）里与本工具相关的一行。</summary>
public sealed record UninstallEntry(
    string DisplayName,
    string Publisher,
    string InstallLocation,
    string DisplayIcon,
    string UninstallString,
    string RegistryPath);

/// <summary>库扫描器共用的工具方法。</summary>
public static class GameLibraryUtil
{
    /// <summary>找主 exe 时向下递归的最大层数（真实游戏常见于 bin\x64\、game\bin\win64\ 等第 3-4 层）。</summary>
    public const int MaxExeSearchDepth = 4;

    /// <summary>单个安装目录最多检查多少个 exe，防止极端目录把扫描拖死。</summary>
    public const int MaxExeCandidates = 800;

    /// <summary>低于这个体积的 exe 基本不是游戏主体（辅助工具/存根）。</summary>
    public const long MinExecutableBytes = 64 * 1024;

    /// <summary>
    /// 这些子串出现在文件名里 → 直接判定为"非游戏主体"，不参与挑选。
    /// 注意都是"去掉扩展名 + 只留字母数字 + 小写"之后的匹配。
    /// </summary>
    private static readonly string[] HardExcludeFragments =
    {
        "setup", "install", "unins", "crash", "report", "vcredist", "dxsetup", "dxwebsetup",
        "unitycrashhandler", "easyanticheat", "battleye", "prereq", "redist", "dotnet",
        "cefsubprocess", "webhelper", "anticheat", "updater", "activation", "cleanup",
        "register", "uninstall", "diagnostic", "eossdk", "steamerrorreporter",
        "vc_redist", "dxwebsetup", "physx", "oalinst",
    };

    /// <summary>这些子串只做"降权"，不是硬排除（包含 launcher 的 exe 只在没有其它候选时才用）。</summary>
    private static readonly string[] SoftPenaltyFragments =
    {
        "launcher", "bootstrapper", "startup",
    };

    /// <summary>明显的工具/编辑器类名字，降权（CS2 目录里的 source1import.exe 就是典型）。</summary>
    private static readonly string[] ToolPenaltyFragments =
    {
        "import", "export", "mdl", "console", "resource", "hammer", "workshop", "demo",
        "dump", "convert", "capture", "recorder", "tool", "editor", "server", "benchmark",
    };

    private static readonly Lazy<List<UninstallEntry>> UninstallCache =
        new(EnumerateUninstallEntriesCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>本机全部卸载项（进程内缓存一次，扫描多个平台时复用）。</summary>
    public static IReadOnlyList<UninstallEntry> UninstallEntries => UninstallCache.Value;

    /// <summary>
    /// 归一化路径：去引号/空白、展开环境变量、统一成反斜杠、去掉结尾多余的反斜杠。
    /// </summary>
    public static string NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim().Trim('"').Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            value = Environment.ExpandEnvironmentVariables(value);
        }
        catch (Exception)
        {
            // 环境变量畸形时按原样使用。
        }

        value = value.Replace('/', '\\');

        var trimmed = value.TrimEnd('\\');
        if (trimmed.Length == 0)
        {
            return value;
        }

        // "D:\" 这种根路径不能把反斜杠吃掉。
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + "\\";
        }

        return trimmed;
    }

    /// <summary>把任意名字压成"只含小写字母数字"的比较键（用来做不区分大小写/标点的匹配）。</summary>
    public static string NormalizeToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>目录是否存在（不抛异常）。</summary>
    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>文件是否存在（不抛异常）。</summary>
    public static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>安全地枚举文件（忽略无权限目录、不跟随重解析点）。</summary>
    public static IEnumerable<string> SafeEnumerateFiles(
        string directory,
        string searchPattern,
        int maxDepth,
        int maxFiles = 20000)
    {
        if (!DirectoryExists(directory))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = maxDepth > 0,
            IgnoreInaccessible = true,
            MaxRecursionDepth = Math.Max(0, maxDepth),
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
        };

        var count = 0;
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(directory, searchPattern, options).GetEnumerator();
        }
        catch (Exception)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception)
                {
                    // 枚举过程中遇到坏目录：直接结束，不抛。
                    yield break;
                }

                if (!moved)
                {
                    break;
                }

                yield return enumerator.Current;

                if (++count >= maxFiles)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>文件名是否被硬排除（一定不是游戏主体）。</summary>
    public static bool IsHardExcludedExecutable(string fileName)
    {
        var token = NormalizeToken(Path.GetFileNameWithoutExtension(fileName));
        return HardExcludeFragments.Any(fragment => token.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// 在一个安装目录里挑"最像游戏主体"的 exe。
    /// 评分 = log10(体积) + 同名加分 - 工具/启动器降权。
    /// 这么设计是因为单纯取最大体积会被 CS2 目录里的 source1import.exe(18MB) 这种开发工具骗到，
    /// 而真正的 cs2.exe 只有 2.9MB。
    /// </summary>
    /// <param name="installDir">安装目录。</param>
    /// <param name="preferredName">期望名字（一般是 installdir / 游戏名），同名或包含其词元会加分。</param>
    /// <param name="notes">诊断信息输出。</param>
    public static string FindMainExecutable(string installDir, string? preferredName, List<string>? notes = null)
    {
        if (!DirectoryExists(installDir))
        {
            return string.Empty;
        }

        var candidates = new List<(string Path, string Token, long Size, double Score)>();
        var skipped = 0;

        try
        {
            foreach (var file in SafeEnumerateFiles(installDir, "*.exe", MaxExeSearchDepth, 40000))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (candidates.Count >= MaxExeCandidates)
                {
                    notes?.Add($"安装目录 exe 数量超过 {MaxExeCandidates} 个，已停止继续枚举。");
                    break;
                }

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    continue;
                }

                if (size < MinExecutableBytes)
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (IsHardExcludedExecutable(name))
                {
                    skipped++;
                    continue;
                }

                candidates.Add((file, NormalizeToken(name), size, ScoreExecutable(name, size, preferredName)));
            }
        }
        catch (Exception ex)
        {
            notes?.Add($"枚举安装目录 exe 失败：{ex.GetType().Name} {ex.Message}");
            return string.Empty;
        }

        if (candidates.Count == 0)
        {
            notes?.Add($"安装目录下没有找到可用的游戏 exe（已排除 {skipped} 个安装器/反作弊类 exe）。");
            return string.Empty;
        }

        var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Size).First();

        // 只有在"优选名"没命中、且最优解本身得分很勉强时，才提示这是猜的。
        var preferredToken = NormalizeToken(preferredName);
        if (preferredToken.Length > 0 && !best.Token.Contains(preferredToken, StringComparison.Ordinal))
        {
            notes?.Add($"未能找到与 \"{preferredName}\" 同名的 exe，按体积+名称评分选中 {Path.GetFileName(best.Path)}。");
        }

        return best.Path;
    }

    private static double ScoreExecutable(string fileName, long size, string? preferredName)
    {
        var token = NormalizeToken(fileName);
        var score = Math.Log10(Math.Max(size, 1024));

        var preferredToken = NormalizeToken(preferredName);
        if (preferredToken.Length > 0)
        {
            if (string.Equals(token, preferredToken, StringComparison.Ordinal))
            {
                score += 2.5;
            }
            // 注意：这里要求长度 ≥6。用 4 会让 "dock.exe" 因为 "mydockfinder" 里含 "dock"
            // 而拿到 +2.0，压过体积更大的 Dock_64.exe（MyDockFinder 实测踩过这个坑）。
            // 32/64 位同族主程序由下面的 SplitStemFragments 分支以更低权重（+1.2）照顾，
            // 这样 MyDockFinder 会选体积最大的 Dock_64.exe，而不是 275KB 的 Mydock.exe。
            else if (token.Contains(preferredToken, StringComparison.Ordinal)
                     || (token.Length >= 6 && preferredToken.Contains(token, StringComparison.Ordinal)))
            {
                score += 2.0;
            }
            else if (SplitTokens(preferredName).Any(t => t.Length >= 4 && token.Contains(t, StringComparison.Ordinal)))
            {
                score += 1.2;
            }
            // 反向：exe 名里的词元被游戏名包含（如 Dock_64.exe 的 "dock" ⊂ MyDockFinder）。
            // 权重刻意低于"包含整个游戏名"，避免小体积的同族 exe 反超体积更大的主程序。
            else if (SplitStemFragments(fileName).Any(f => f.Length >= 4 && preferredToken.Contains(f, StringComparison.Ordinal)))
            {
                score += 1.2;
            }
        }

        var penalty = 0.0;
        if (SoftPenaltyFragments.Any(f => token.Contains(f, StringComparison.Ordinal)))
        {
            penalty += 1.2;
        }

        foreach (var fragment in ToolPenaltyFragments)
        {
            if (token.Contains(fragment, StringComparison.Ordinal))
            {
                penalty += 1.5;
            }
        }

        return score - Math.Min(penalty, 3.0);
    }

    /// <summary>把一个显示名拆成有意义的小写词元（长度 ≥2）。</summary>
    public static IEnumerable<string> SplitTokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        foreach (var part in name.Split(
                     new[] { ' ', '-', '_', '.', ':', '(', ')', '[', ']', '\'', '’', ',', '™', '®', '/', '\\' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = NormalizeToken(part);
            if (token.Length >= 2)
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// 把 exe 文件名按分隔符和数字切词（Dock_64 → dock / 64，wallpaper64 → wallpaper），
    /// 用于识别"名字里带位数后缀"的同族主程序。数字单独成词，不参与 ≥4 长度的匹配。
    /// </summary>
    private static IEnumerable<string> SplitStemFragments(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(stem))
        {
            return Array.Empty<string>();
        }

        var spaced = new string(stem.Select(ch => ch >= '0' && ch <= '9' ? ' ' : ch).ToArray());
        return SplitTokens(spaced);
    }

    /// <summary>
    /// 这些词在目录名里太常见（updater/launcher/client…），拿它们当"同名证据"会大面积误伤。
    /// 实测：%LOCALAPPDATA%\pc_update_helper 因为和进程名 wetype_update 共享 "update"，
    /// 被误配成"微信输入法"的存档目录。
    /// </summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "update", "updater", "updates", "upgrade", "setup", "install", "installer", "installation",
        "launcher", "launch", "boot", "bootstrapper", "startup", "starter", "studio", "studios",
        "software", "solutions", "technology", "technologies", "company", "corporation", "limited",
        "interactive", "entertainment", "digital", "media", "network", "networks", "international",
        "client", "server", "service", "services", "helper", "agent", "daemon", "host", "worker",
        "system", "windows", "default", "common", "shared", "share", "public", "global",
        "game", "games", "gaming", "engine", "tool", "tools", "utility", "utilities",
        "data", "files", "file", "folder", "dir", "directory", "path", "temp", "temporary",
        "cache", "cached", "log", "logs", "logging", "crash", "crashes", "report", "reports",
        "config", "configs", "setting", "settings", "preference", "preferences", "option", "options",
        "plugin", "plugins", "module", "modules", "extension", "extensions", "package", "packages",
        "version", "versions", "release", "debug", "stable", "beta", "alpha", "preview", "portable",
        "runtime", "redist", "redistributable", "framework", "library", "libraries", "support",
        "resource", "resources", "assets", "asset", "content", "contents", "media", "source", "src",
        "user", "users", "profile", "profiles", "account", "accounts", "local", "roaming", "backup",
        "notify", "notification", "notifications", "message", "messages", "network", "update64",
        "x64", "x86", "win32", "win64", "amd64", "native", "bin", "binaries", "release64",
        "main", "core", "base", "app", "apps", "application", "program", "programs",
    };

    /// <summary>判断两个名字是否"看起来是同一个东西"（用于把 LocalLow/存档目录匹配到游戏）。</summary>
    public static bool LooksRelated(string? left, string? right)
    {
        var a = NormalizeToken(left);
        var b = NormalizeToken(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        // 短名字必须完全相等，避免 "cs2" 命中 "watchdogs2" 这类误伤。
        if (a.Length >= 4 && b.Contains(a, StringComparison.Ordinal))
        {
            return true;
        }

        if (b.Length >= 4 && a.Contains(b, StringComparison.Ordinal))
        {
            return true;
        }

        return SplitTokens(left).Any(t => IsDistinctiveToken(t) && b.Contains(t, StringComparison.Ordinal))
               || SplitTokens(right).Any(t => IsDistinctiveToken(t) && a.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>这个词元够不够"有辨识度"（够长且不是 update/launcher/client 这类通用词）。</summary>
    private static bool IsDistinctiveToken(string token)
        => token.Length >= 4 && !GenericTokens.Contains(token);

    /// <summary>出现这些顶层目录名，基本可以确认是游戏安装（引擎/资源结构）。</summary>
    private static readonly string[] GameMarkerDirectories =
    {
        "*_Data", "Engine", "Content", "Binaries", "GameData", "Mods", "Saved",
    };

    /// <summary>主程序落在这些路径片段里，也说明是游戏（UE 的 Binaries、Unity 的 *_Data 等）。</summary>
    private static readonly string[] GameExePathMarkers =
    {
        @"\binaries\", @"\engine\", @"_data\", @"\gamedata\", @"\mods\", @"\game\bin\",
    };

    /// <summary>
    /// 安装目录是否"有游戏的样子"：出现引擎/资源目录结构，或主程序在引擎布局目录里。
    /// 实测用途：把"三角洲行动"（顶层有 Engine，exe 在 DeltaForce\Binaries\Win64\）
    /// 与腾讯的 DF Launcher / QQ / 微信 / WeType 区分开。注意不要把 Data、assets 这类
    /// 过于通用的目录名当成标记 —— DF Launcher 有 data、WorkBuddy 有 Assets，都会误判。
    /// </summary>
    public static bool LooksLikeGameInstall(string installDir, string? exePath, int markerSearchDepth = 1)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !DirectoryExists(installDir))
        {
            return false;
        }

        if (exePath is { Length: > 0 })
        {
            var exeLower = exePath.ToLowerInvariant().Replace('/', '\\');
            if (GameExePathMarkers.Any(m => exeLower.Contains(m, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        var depth = Math.Clamp(markerSearchDepth, 0, 4);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = depth > 1,
            MaxRecursionDepth = Math.Max(depth, 1),
            MatchType = MatchType.Simple,
        };

        foreach (var marker in GameMarkerDirectories)
        {
            try
            {
                if (Directory.EnumerateDirectories(installDir, marker, options).Any())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 读不了就继续试下一个标记。
            }
        }

        return false;
    }

    /// <summary>平台客户端/启动器自身（不是游戏）的标准化名字。</summary>
    private static readonly HashSet<string> PlatformClientTokens = new(StringComparer.Ordinal)
    {
        "steam", "steamclient", "epicgameslauncher", "epicgames", "battlenet", "blizzardbattle.net",
        "blizzardbrowser", "goggalaxy", "galaxyclient", "ubisoftconnect", "uplay", "ubisoftgamelauncher",
        "wegame", "wegamesetup", "tencentwegame", "wegameclient", "tgp", "eadesktop", "eaapp", "origin",
        "riotclient", "rockstargameslauncher", "bethesdanetlauncher", "playnite", "xbox", "gamingapp",
        "gamingservices", "itch", "itchio", "amazongames", "primegaming", "nvidiaapp", "geforcenow",
        "paradoxlauncher", "egs", "heroic", "lutris", "wemod", "wegamedl",
    };

    /// <summary>平台客户端自身的安装目录后缀（这些目录下不会放着"该平台的游戏本体"）。</summary>
    private static readonly string[] PlatformClientDirectorySuffixes =
    {
        @"\ubisoft game launcher", @"\epic games\launcher", @"\battle.net", @"\gog galaxy",
        @"\wegame", @"\riot client", @"\rockstar games\launcher", @"\steam", @"\steam\bin",
    };

    /// <summary>这个名字是不是平台客户端/启动器本身（不能当成游戏）。</summary>
    public static bool IsPlatformClientName(string? displayName)
        => PlatformClientTokens.Contains(NormalizeToken(displayName));

    /// <summary>这个安装目录是不是平台客户端自己的目录。</summary>
    public static bool IsPlatformClientDirectory(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return false;
        }

        var normalized = installDir.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return PlatformClientDirectorySuffixes.Any(s => normalized.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>列出全部注册表卸载项（HKLM/HKCU × 64/32 位视图）。任何异常都不外抛。</summary>
    private static List<UninstallEntry> EnumerateUninstallEntriesCore()
    {
        var result = new List<UninstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var locations = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
        };

        const string subKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var (hive, view) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(subKeyPath);
                if (uninstallKey is null)
                {
                    continue;
                }

                foreach (var name in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var item = uninstallKey.OpenSubKey(name);
                        if (item is null)
                        {
                            continue;
                        }

                        var displayName = (item.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        if (displayName.Length == 0)
                        {
                            continue;
                        }

                        var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                        var viewName = view == RegistryView.Registry32 ? "WOW6432Node\\" : string.Empty;
                        var registryPath = $@"{hiveName}\SOFTWARE\{viewName}{subKeyPath}\{name}";
                        if (!seen.Add(displayName + "|" + registryPath))
                        {
                            continue;
                        }

                        result.Add(new UninstallEntry(
                            displayName,
                            (item.GetValue("Publisher") as string ?? string.Empty).Trim(),
                            NormalizePath(item.GetValue("InstallLocation") as string),
                            (item.GetValue("DisplayIcon") as string ?? string.Empty).Trim(),
                            (item.GetValue("UninstallString") as string ?? string.Empty).Trim(),
                            registryPath));
                    }
                    catch (Exception)
                    {
                        // 单个键读失败（权限/类型异常）不影响其它键。
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // 整个视图不可读：跳过。
            }
            catch (Exception)
            {
                // 其它异常同样跳过，扫描器不允许崩。
            }
        }

        return result;
    }

    /// <summary>读取单个注册表字符串值（读不到/没权限一律返回空串，不抛）。</summary>
    public static string ReadRegistryValue(RegistryHive hive, RegistryView view, string subKeyPath, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKeyPath);
            var raw = key?.GetValue(valueName);
            return raw switch
            {
                string text => text.Trim(),
                string[] array when array.Length > 0 => array[0].Trim(),
                _ => string.Empty,
            };
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 枚举某个注册表键下的全部子键，并对每个子键取"第一个非空的候选值"。
    /// Battle.net / Ubisoft / GOG / WeGame 都用这种 <c>&lt;游戏&gt; → InstallPath</c> 的结构。
    /// </summary>
    public static List<(string SubKeyName, string KeyPath, string ValueName, string Value)> ReadRegistryChildren(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        params string[] valueNames)
    {
        var result = new List<(string, string, string, string)>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var parent = baseKey.OpenSubKey(subKeyPath);
            if (parent is null)
            {
                return result;
            }

            foreach (var childName in parent.GetSubKeyNames())
            {
                try
                {
                    using var child = parent.OpenSubKey(childName);
                    if (child is null)
                    {
                        continue;
                    }

                    foreach (var valueName in valueNames)
                    {
                        var raw = child.GetValue(valueName);
                        var value = raw switch
                        {
                            string text => text.Trim(),
                            string[] array when array.Length > 0 => array[0].Trim(),
                            _ => string.Empty,
                        };

                        if (value.Length > 0)
                        {
                            result.Add((childName, $@"{subKeyPath}\{childName}", valueName, value));
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    // 单个子键读失败不影响其它。
                }
            }
        }
        catch (Exception)
        {
            // 整个键不存在/无权限：返回空列表。
        }

        return result;
    }

    /// <summary>从 exe 全路径推进程名（不含扩展名）。</summary>
    public static string DeriveProcessName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(executablePath);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 从 DisplayIcon / UninstallString 里尽量抠出一个安装目录（有些游戏只写这两项）。
    /// </summary>
    public static string TryDeriveInstallDir(UninstallEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            return entry.InstallLocation;
        }

        foreach (var raw in new[] { entry.DisplayIcon, entry.UninstallString })
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // DisplayIcon 常见形式："C:\Game\game.exe,0"
            var candidate = raw.Split(',')[0].Trim().Trim('"');
            if (candidate.Length == 0)
            {
                continue;
            }

            try
            {
                if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrEmpty(dir) && DirectoryExists(dir))
                    {
                        return NormalizePath(dir);
                    }
                }
                else if (DirectoryExists(candidate))
                {
                    return NormalizePath(candidate);
                }
            }
            catch (Exception)
            {
                // 忽略，继续尝试下一项。
            }
        }

        return string.Empty;
    }
}
