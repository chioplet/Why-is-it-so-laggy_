// ============================================================================
//  Gpd.Core / Games / GogScanner.cs
//  ---------------------------------------------------------------------------
//  GOG 库扫描：GOG Galaxy / 离线安装器会把每个游戏写在
//  HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<游戏ID> 下，值为 path / gameName / exe。
//  另外用卸载项里 Publisher 含 GOG 的条目兜底。只读注册表。
// ============================================================================

using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>GOG 平台扫描器。</summary>
public sealed class GogScanner : IGameLibraryScanner
{
    public string Name => "GOG";

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 安装ID → (注册表键路径, 值名 → 值)
        var records = new Dictionary<string, (string KeyPath, Dictionary<string, string> Values)>(StringComparer.OrdinalIgnoreCase);
        var sawRoot = false;

        foreach (var (hive, view, subKeyPath) in new (RegistryHive, RegistryView, string)[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\GOG.com\Games"),
                     (RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\GOG.com\Games"),
                     (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\GOG.com\Games"),
                 })
        {
            foreach (var valueName in new[] { "path", "gameName", "exe" })
            {
                foreach (var (childName, keyPath, _, value) in GameLibraryUtil.ReadRegistryChildren(
                             hive, view, subKeyPath, valueName))
                {
                    sawRoot = true;
                    if (!records.TryGetValue(childName, out var record))
                    {
                        record = (keyPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                        records[childName] = record;
                    }

                    record.Values[valueName] = value;
                }
            }
        }

        foreach (var (id, record) in records)
        {
            if (!record.Values.TryGetValue("path", out var path) || path.Length == 0)
            {
                var knownName = record.Values.TryGetValue("gameName", out var gn) && gn.Length > 0 ? gn : id;
                warnings.Add($"GOG：\"{knownName}\"（{record.KeyPath}）缺少 path 值，无法定位安装目录。");
                continue;
            }

            var name = record.Values.TryGetValue("gameName", out var gameName) && gameName.Length > 0
                ? gameName
                : $"GOG 游戏 {id}";

            var game = Build(name, path, $"注册表 {record.KeyPath}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                game.AppId = id;
                yield return game;
            }
        }

        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            if (!entry.Publisher.Contains("GOG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var game = Build(entry.DisplayName, GameLibraryUtil.TryDeriveInstallDir(entry),
                $"卸载项 {entry.RegistryPath}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                yield return game;
            }
        }

        if (seen.Count == 0)
        {
            warnings.Add(sawRoot
                ? "GOG：注册表有 GOG.com\\Games 键但没有可用的游戏路径，本机没有已安装的 GOG 游戏。"
                : "GOG：未检测到 GOG 安装记录，本机可能没装 GOG Galaxy 或没有 GOG 游戏。");
        }
    }

    private static GameInfo? Build(string name, string installDir, string source, List<string> warnings)
    {
        var normalized = GameLibraryUtil.NormalizePath(installDir);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (!GameLibraryUtil.DirectoryExists(normalized))
        {
            warnings.Add($"GOG：\"{name}\" 的安装目录已不存在（{normalized}），已跳过。");
            return null;
        }

        var exe = GameLibraryUtil.FindMainExecutable(normalized, name, null);
        if (exe.Length == 0)
        {
            warnings.Add($"GOG：\"{name}\"（{normalized}）没能识别出主程序 exe。");
        }

        return new GameInfo
        {
            Name = name,
            Platform = GamePlatform.Gog,
            InstallDir = normalized,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            DiscoverySource = source,
        };
    }
}
