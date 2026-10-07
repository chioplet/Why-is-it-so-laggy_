// ============================================================================
//  Gpd.Core / Games / SteamLibraryScanner.cs
//  ---------------------------------------------------------------------------
//  Steam 库扫描：注册表定位 Steam 根目录 → libraryfolders.vdf 列出全部库 →
//  每个 steamapps\appmanifest_*.acf 解析出一个已安装应用；
//  再从 userdata\<id>\config\localconfig.vdf 补充最近游玩时间与总时长。
//  只读，不改动 Steam 的任何文件。
// ============================================================================

using System.Globalization;
using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>Steam 平台扫描器。</summary>
public sealed class SteamLibraryScanner : IGameLibraryScanner
{
    public string Name => "Steam";

    /// <summary>这些名字不是游戏本体（运行时/工具/原声带），不产出 GameInfo。</summary>
    private static readonly string[] NonGameNameFragments =
    {
        "redistributable", "steamworks", "steam linux runtime", "proton",
        "steamvr", "soundtrack", "dedicated server", "steam audio",
    };

    /// <summary>已知的非游戏 AppId（Steam 官方运行时组件）。</summary>
    private static readonly HashSet<string> NonGameAppIds = new(StringComparer.Ordinal)
    {
        "228980",  // Steamworks Common Redistributables
        "1070560", // Steam Linux Runtime 1.0 (scout)
        "1391110", // Steam Linux Runtime 2.0 (soldier)
        "1493710", // Proton Experimental
        "1628350", // Steam Linux Runtime 3.0 (sniper)
        "1887720", // Proton 7.0
        "2348590", // Proton 8.0
        "2289890", // Proton 9.0
        "961940",  // Steam Audio
    };

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var steamRoot = FindSteamRoot(warnings);
        if (steamRoot.Length == 0)
        {
            warnings.Add("Steam：未能定位 Steam 安装目录（注册表 HKCU\\SOFTWARE\\Valve\\Steam\\SteamPath 等位置均无有效路径），跳过 Steam 扫描。");
            yield break;
        }

        var libraries = CollectLibraries(steamRoot, warnings);
        var playStats = CollectPlayStats(steamRoot, warnings);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!GameLibraryUtil.DirectoryExists(steamApps))
            {
                continue;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex)
            {
                warnings.Add($"Steam：读取库 \"{steamApps}\" 失败（{ex.GetType().Name}），已跳过该库。");
                continue;
            }

            foreach (var manifest in manifests)
            {
                var game = ParseManifest(manifest, library, warnings);
                if (game is null)
                {
                    continue;
                }

                if (!seen.Add(game.AppId + "|" + game.InstallDir))
                {
                    continue;
                }

                if (playStats.TryGetValue(game.AppId, out var stat))
                {
                    game.LastPlayed = stat.LastPlayed;
                    game.PlayTime = stat.PlayTime;
                }

                yield return game;
            }
        }
    }

    /// <summary>定位 Steam 根目录：注册表优先，其次常见默认安装位置。</summary>
    private static string FindSteamRoot(List<string> warnings)
    {
        foreach (var (hive, view, subKey, valueName) in new (RegistryHive, RegistryView, string, string)[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "SteamPath"),
                     (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "SteamPath"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                var raw = key?.GetValue(valueName) as string;
                var path = GameLibraryUtil.NormalizePath(raw);
                if (path.Length > 0 && GameLibraryUtil.DirectoryExists(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // 单个注册表位置读不到就试下一个。
            }
        }

        foreach (var candidate in EnumerateDefaultSteamRoots())
        {
            if (GameLibraryUtil.DirectoryExists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 枚举常见的 Steam 安装目录：先试标准 Program Files 位置，再扫每个固定磁盘上的
    /// 常见文件夹名。不要写死某一台机器的盘符——Steam 装在 D:\、E:\ 都很常见。
    /// </summary>
    private static IEnumerable<string> EnumerateDefaultSteamRoots()
    {
        yield return @"C:\Program Files (x86)\Steam";
        yield return @"C:\Program Files\Steam";

        string[] folderNames = ["Steam", "SteamLibrary", @"Games\Steam", @"Program Files (x86)\Steam"];

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            var usable = false;
            string? root = null;
            try
            {
                usable = drive.DriveType == DriveType.Fixed && drive.IsReady;
                root = drive.RootDirectory.FullName;
            }
            catch (Exception)
            {
                // 读不到盘符信息就跳过这张盘。
            }

            if (!usable || string.IsNullOrEmpty(root))
            {
                continue;
            }

            foreach (var folderName in folderNames)
            {
                yield return Path.Combine(root, folderName);
            }
        }
    }

    /// <summary>从 libraryfolders.vdf 收集全部库根目录（保序去重，含 Steam 自身目录）。</summary>
    private static List<string> CollectLibraries(string steamRoot, List<string> warnings)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            var normalized = GameLibraryUtil.NormalizePath(path);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        Add(steamRoot);

        var candidates = new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        };

        var parsedAny = false;
        foreach (var file in candidates)
        {
            if (!GameLibraryUtil.FileExists(file))
            {
                continue;
            }

            VdfNode? root;
            try
            {
                root = VdfParser.ParseFile(file);
            }
            catch (Exception ex)
            {
                warnings.Add($"Steam：解析 \"{file}\" 失败（{ex.Message}）。");
                continue;
            }

            if (root is null)
            {
                warnings.Add($"Steam：无法读取 \"{file}\"，已跳过。");
                continue;
            }

            // 新版："libraryfolders" { "0" { "path" "C:\\..." } }；老版："LibraryFolders" { "1" "D:\\..." }
            var container = root.Child("libraryfolders") ?? root.Child("LibraryFolders");
            if (container is null)
            {
                continue;
            }

            parsedAny = true;
            foreach (var child in container.Children)
            {
                if (child.IsObject)
                {
                    var path = child.ChildValue("path") ?? child.ChildValue("Path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        Add(path);
                    }
                }
                else if (child.Value is { Length: > 0 } raw && LooksLikePath(raw))
                {
                    Add(raw);
                }
            }
        }

        if (!parsedAny)
        {
            warnings.Add("Steam：未找到可解析的 libraryfolders.vdf，仅扫描 Steam 自身目录。");
        }

        return result;
    }

    private static bool LooksLikePath(string value)
        => value.Contains(':') || value.Contains('\\') || value.Contains('/');

    /// <summary>把一个 appmanifest_*.acf 解析成 GameInfo；不是游戏则返回 null。</summary>
    private static GameInfo? ParseManifest(string manifestPath, string library, List<string> warnings)
    {
        VdfNode? root;
        try
        {
            root = VdfParser.ParseFile(manifestPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"Steam：解析 \"{Path.GetFileName(manifestPath)}\" 失败（{ex.Message}）。");
            return null;
        }

        var state = root?.Child("AppState");
        if (state is null)
        {
            warnings.Add($"Steam：\"{Path.GetFileName(manifestPath)}\" 缺少 AppState 节点，已跳过。");
            return null;
        }

        var appId = (state.ChildValue("appid") ?? string.Empty).Trim();
        var name = (state.ChildValue("name") ?? string.Empty).Trim();
        var installDirName = (state.ChildValue("installdir") ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            name = appId.Length > 0 ? $"未命名应用 {appId}" : Path.GetFileNameWithoutExtension(manifestPath);
        }

        if (NonGameAppIds.Contains(appId))
        {
            return null;
        }

        var lowerName = name.ToLowerInvariant();
        if (NonGameNameFragments.Any(f => lowerName.Contains(f, StringComparison.Ordinal)))
        {
            return null;
        }

        if (installDirName.Length == 0)
        {
            warnings.Add($"Steam：\"{name}\"（appid {appId}）的清单缺少 installdir，无法定位安装目录。");
            return null;
        }

        var installDir = GameLibraryUtil.NormalizePath(Path.Combine(library, "steamapps", "common", installDirName));
        if (!GameLibraryUtil.DirectoryExists(installDir))
        {
            warnings.Add($"Steam：\"{name}\" 记录的安装目录不存在（{installDir}），可能已被手动删除，已跳过。");
            return null;
        }

        var notes = new List<string>();
        var exe = GameLibraryUtil.FindMainExecutable(installDir, installDirName, notes);
        if (exe.Length == 0)
        {
            warnings.Add($"Steam：\"{name}\"（{installDir}）没能识别出主程序 exe。");
        }

        var info = new GameInfo
        {
            Name = name,
            Platform = GamePlatform.Steam,
            InstallDir = installDir,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            AppId = appId,
            DiscoverySource = $"Steam 清单 {Path.GetFileName(manifestPath)}（库 {library}）",
        };

        // StateFlags 的 4 号位表示"完整安装"；缺少它说明游戏没下完，如实提示。
        if (int.TryParse(state.ChildValue("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
            && (flags & 4) == 0)
        {
            warnings.Add($"Steam：\"{name}\" 的 StateFlags={flags}，不是完整安装状态（可能未下载完）。");
        }

        if (long.TryParse(state.ChildValue("LastPlayed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastPlayed)
            && lastPlayed > 0)
        {
            info.LastPlayed = DateTimeOffset.FromUnixTimeSeconds(lastPlayed);
        }

        return info;
    }

    /// <summary>从 userdata 的 localconfig.vdf 汇总每个 AppId 的游玩时长 / 最近游玩。</summary>
    private static Dictionary<string, (DateTimeOffset? LastPlayed, TimeSpan? PlayTime)> CollectPlayStats(
        string steamRoot,
        List<string> warnings)
    {
        var result = new Dictionary<string, (DateTimeOffset? LastPlayed, TimeSpan? PlayTime)>(StringComparer.Ordinal);
        var userData = Path.Combine(steamRoot, "userdata");
        if (!GameLibraryUtil.DirectoryExists(userData))
        {
            return result;
        }

        string[] userDirs;
        try
        {
            userDirs = Directory.GetDirectories(userData);
        }
        catch (Exception ex)
        {
            warnings.Add($"Steam：枚举 userdata 失败（{ex.GetType().Name}），将无法提供游玩时长。");
            return result;
        }

        foreach (var dir in userDirs)
        {
            var config = Path.Combine(dir, "config", "localconfig.vdf");
            if (!GameLibraryUtil.FileExists(config))
            {
                continue;
            }

            VdfNode? root;
            try
            {
                root = VdfParser.ParseFile(config);
            }
            catch (Exception)
            {
                continue;
            }

            var apps = root?.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            if (apps is null)
            {
                continue;
            }

            foreach (var app in apps.Children)
            {
                var appId = app.Key.Trim();
                if (appId.Length == 0)
                {
                    continue;
                }

                DateTimeOffset? lastPlayed = null;
                if (long.TryParse(app.ChildValue("LastPlayed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                    && epoch > 0)
                {
                    lastPlayed = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }

                TimeSpan? playTime = null;
                if (long.TryParse(app.ChildValue("Playtime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    && minutes >= 0)
                {
                    playTime = TimeSpan.FromMinutes(minutes);
                }

                if (lastPlayed is null && playTime is null)
                {
                    continue;
                }

                // 同一台机器可能有多份 userdata，取游玩最多 / 最近的那份。
                if (result.TryGetValue(appId, out var existing))
                {
                    var bestLast = existing.LastPlayed;
                    if (lastPlayed is not null && (bestLast is null || lastPlayed > bestLast))
                    {
                        bestLast = lastPlayed;
                    }

                    var bestPlay = existing.PlayTime;
                    if (playTime is not null && (bestPlay is null || playTime > bestPlay))
                    {
                        bestPlay = playTime;
                    }

                    result[appId] = (bestLast, bestPlay);
                }
                else
                {
                    result[appId] = (lastPlayed, playTime);
                }
            }
        }

        return result;
    }
}
