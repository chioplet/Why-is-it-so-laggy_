// ============================================================================
//  Gpd.Core / Games / GameLibraryService.cs
//  ---------------------------------------------------------------------------
//  扫描入口：按顺序跑各个平台扫描器 → 合并去重 → 补齐配置文件与画质解读。
//  任何一个扫描器抛异常都不会影响其它平台（逐个 try/catch 并写 warnings）。
// ============================================================================

namespace Gpd.Core.Games;

/// <summary>游戏库扫描的统一入口。</summary>
public static class GameLibraryService
{
    /// <summary>默认启用的扫描器（不含"独立安装"，那个需要显式打开）。</summary>
    public static IReadOnlyList<IGameLibraryScanner> CreateDefaultScanners() => new List<IGameLibraryScanner>
    {
        new SteamLibraryScanner(),
        new EpicLibraryScanner(),
        new BattleNetScanner(),
        new UbisoftScanner(),
        new GogScanner(),
        new WeGameScanner(),
        new XboxScanner(),
    };

    /// <summary>全部扫描器，含默认关闭的"独立安装"。</summary>
    public static IReadOnlyList<IGameLibraryScanner> CreateAllScanners()
    {
        var scanners = new List<IGameLibraryScanner>(CreateDefaultScanners())
        {
            new StandaloneScanner(),
        };
        return scanners;
    }

    /// <summary>
    /// 扫描本机游戏库。
    /// </summary>
    /// <param name="warnings">收集诊断信息（缺少权限、目录不存在、识别失败等）。</param>
    /// <param name="includeStandalone">是否启用误报率较高的"独立安装"扫描器，默认 false。</param>
    /// <param name="attachConfigFiles">是否顺便定位并解析配置文件，默认 true。</param>
    public static List<GameInfo> ScanAll(
        List<string> warnings,
        bool includeStandalone = false,
        bool attachConfigFiles = true)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var scanners = includeStandalone ? CreateAllScanners() : CreateDefaultScanners();
        return Scan(warnings, scanners, attachConfigFiles);
    }

    /// <summary>用指定的扫描器集合扫描（便于测试与扩展）。</summary>
    public static List<GameInfo> Scan(
        List<string> warnings,
        IEnumerable<IGameLibraryScanner> scanners,
        bool attachConfigFiles = true)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(scanners);

        var games = new List<GameInfo>();

        foreach (var scanner in scanners)
        {
            var localWarnings = new List<string>();
            try
            {
                foreach (var game in scanner.Scan(localWarnings))
                {
                    if (game is not null)
                    {
                        games.Add(game);
                    }
                }
            }
            catch (Exception ex)
            {
                localWarnings.Add($"{scanner.Name}：扫描器内部异常（{ex.GetType().Name} {ex.Message}），该平台结果不完整。");
            }

            warnings.AddRange(localWarnings);
        }

        var merged = MergeDuplicates(games, warnings);

        if (attachConfigFiles)
        {
            foreach (var game in merged)
            {
                try
                {
                    ConfigFileLocator.Populate(game, warnings);
                }
                catch (Exception ex)
                {
                    warnings.Add($"{game.Name}：定位配置文件时异常（{ex.GetType().Name} {ex.Message}）。");
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// 同一个安装目录可能被多个来源发现（例如 Steam 的卸载项又会出现在独立安装扫描里），
    /// 这里按安装目录合并，保留信息更全的那份（有 exe 的优先、有平台信息的优先）。
    /// </summary>
    private static List<GameInfo> MergeDuplicates(List<GameInfo> games, List<string> warnings)
    {
        var byKey = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var game in games)
        {
            var key = game.InstallDir.Length > 0
                ? GameLibraryUtil.NormalizePath(game.InstallDir)
                : $"{game.Platform}|{game.Name}";

            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = game;
                order.Add(key);
                continue;
            }

            var winner = BetterOf(existing, game);
            if (!ReferenceEquals(winner, existing))
            {
                warnings.Add($"游戏库：\"{game.Name}\"（{game.PlatformName}）与 \"{existing.Name}\"（{existing.PlatformName}）"
                             + $"指向同一目录，已保留信息更全的一条。");
                byKey[key] = winner;
            }

            // 合并能互补的字段。
            if (winner.ExecutablePath.Length == 0 && !ReferenceEquals(winner, game) && game.ExecutablePath.Length > 0)
            {
                winner.ExecutablePath = game.ExecutablePath;
                winner.ProcessName = game.ProcessName;
            }

            winner.LastPlayed ??= game.LastPlayed;
            winner.PlayTime ??= game.PlayTime;
        }

        return order.Select(k => byKey[k])
            .OrderBy(g => g.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static GameInfo BetterOf(GameInfo left, GameInfo right)
    {
        var leftScore = Score(left);
        var rightScore = Score(right);
        return rightScore > leftScore ? right : left;
    }

    private static int Score(GameInfo game)
    {
        var score = 0;
        if (game.ExecutablePath.Length > 0)
        {
            score += 4;
        }

        if (game.AppId.Length > 0)
        {
            score += 2;
        }

        if (game.Platform != GamePlatform.Standalone)
        {
            score += 2;
        }

        if (game.DiscoverySource.Length > 0)
        {
            score += 1;
        }

        return score;
    }
}
