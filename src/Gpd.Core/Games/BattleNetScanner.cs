// ============================================================================
//  Gpd.Core / Games / BattleNetScanner.cs
//  ---------------------------------------------------------------------------
//  Battle.net 库扫描：暴雪把每个游戏装在
//  HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\<游戏名> 下并写 InstallPath。
//  另外用卸载项里 Publisher = Blizzard 的条目兜底。只读注册表。
// ============================================================================

using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>Battle.net 平台扫描器。</summary>
public sealed class BattleNetScanner : IGameLibraryScanner
{
    public string Name => "Battle.net";

    /// <summary>这两个子键是客户端自己，不是游戏。</summary>
    private static readonly string[] ClientSubKeys = { "Battle.net", "Launcher", "Blizzard Browser", "Agent" };

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        var probed = false;

        foreach (var (hive, view, subKeyPath) in new (RegistryHive, RegistryView, string)[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Blizzard Entertainment"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Blizzard Entertainment"),
                 })
        {
            probed = true;
            foreach (var (childName, keyPath, valueName, value) in GameLibraryUtil.ReadRegistryChildren(
                         hive, view, subKeyPath, "InstallPath", "Path"))
            {
                if (ClientSubKeys.Any(c => c.Equals(childName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                found = true;
                var game = Build(childName, value, $"注册表 {keyPath} 的 {valueName}", warnings);
                if (game is not null && seen.Add(game.InstallDir))
                {
                    yield return game;
                }
            }
        }

        // 兜底：卸载项里 Publisher 含 Blizzard 的。
        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            if (!entry.Publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)
                && !entry.Publisher.Contains("暴雪", StringComparison.Ordinal))
            {
                continue;
            }

            found = true;
            var installDir = GameLibraryUtil.TryDeriveInstallDir(entry);
            var game = Build(entry.DisplayName, installDir, $"卸载项 {entry.RegistryPath}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                yield return game;
            }
        }

        if (!found)
        {
            warnings.Add(probed
                ? "Battle.net：未检测到暴雪游戏注册表项，本机可能没装 Battle.net 客户端或没有暴雪游戏。"
                : "Battle.net：扫描未执行。");
        }
    }

    private static GameInfo? Build(string name, string installDir, string source, List<string> warnings)
    {
        var normalized = GameLibraryUtil.NormalizePath(installDir);
        if (normalized.Length == 0)
        {
            warnings.Add($"Battle.net：\"{name}\" 没有可用的安装路径（{source}）。");
            return null;
        }

        if (!GameLibraryUtil.DirectoryExists(normalized))
        {
            warnings.Add($"Battle.net：\"{name}\" 的安装目录已不存在（{normalized}），已跳过。");
            return null;
        }

        var exe = GameLibraryUtil.FindMainExecutable(normalized, name, null);
        if (exe.Length == 0)
        {
            warnings.Add($"Battle.net：\"{name}\"（{normalized}）没能识别出主程序 exe。");
        }

        return new GameInfo
        {
            Name = name,
            Platform = GamePlatform.BattleNet,
            InstallDir = normalized,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            DiscoverySource = source,
        };
    }
}
