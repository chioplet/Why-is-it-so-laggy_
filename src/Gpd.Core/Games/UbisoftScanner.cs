// ============================================================================
//  Gpd.Core / Games / UbisoftScanner.cs
//  ---------------------------------------------------------------------------
//  Ubisoft Connect 库扫描：
//  HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<安装号> 的 InstallDir。
//  另外用卸载项里 Publisher 含 Ubisoft 的条目兜底。只读注册表。
// ============================================================================

using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>Ubisoft Connect 平台扫描器。</summary>
public sealed class UbisoftScanner : IGameLibraryScanner
{
    public string Name => "Ubisoft Connect";

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        // 安装号 → 安装目录；同时也记录一下游戏名（如果有）。
        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sawInstallRoot = false;

        foreach (var (hive, view, subKeyPath) in new (RegistryHive, RegistryView, string)[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Ubisoft\Launcher\Installs"),
                     (RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs"),
                     (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Ubisoft\Launcher\Installs"),
                 })
        {
            var children = GameLibraryUtil.ReadRegistryChildren(
                hive, view, subKeyPath, "InstallDir", "InstallLocation", "Path");
            if (children.Count > 0)
            {
                sawInstallRoot = true;
            }

            foreach (var (childName, _, _, value) in children)
            {
                var normalized = GameLibraryUtil.NormalizePath(value);
                if (normalized.Length > 0)
                {
                    byId[childName] = normalized;
                }
            }

            foreach (var (childName, _, _, value) in GameLibraryUtil.ReadRegistryChildren(
                         hive, view, subKeyPath, "Name", "DisplayName", "GameName"))
            {
                if (value.Length > 0)
                {
                    names[childName] = value;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, installDir) in byId)
        {
            var name = names.TryGetValue(id, out var n) && n.Length > 0 ? n : $"Ubisoft 游戏 {id}";
            var game = Build(name, installDir, $"注册表 Ubisoft\\Launcher\\Installs\\{id}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                game.AppId = id;
                yield return game;
            }
        }

        var skippedClients = 0;
        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            if (!entry.Publisher.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)
                && !entry.Publisher.Contains("育碧", StringComparison.Ordinal))
            {
                continue;
            }

            // Ubisoft Connect / Uplay 启动器自身的卸载项也在这一栏里（本机实测：Publisher=Ubisoft、
            // 安装目录 C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher），不能当成游戏。
            var installDir = GameLibraryUtil.TryDeriveInstallDir(entry);
            if (GameLibraryUtil.IsPlatformClientName(entry.DisplayName)
                || GameLibraryUtil.IsPlatformClientDirectory(installDir))
            {
                skippedClients++;
                continue;
            }

            var game = Build(entry.DisplayName, installDir, $"卸载项 {entry.RegistryPath}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                yield return game;
            }
        }

        if (skippedClients > 0)
        {
            warnings.Add($"Ubisoft Connect：排除了 {skippedClients} 个启动器自身的卸载项（Ubisoft Connect / Uplay 不是游戏）。");
        }

        if (seen.Count == 0)
        {
            warnings.Add(sawInstallRoot
                ? "Ubisoft Connect：注册表有 Ubisoft\\Launcher\\Installs 键但没有任何游戏记录，本机没有已安装的 Ubisoft 游戏。"
                : "Ubisoft Connect：未检测到 Ubisoft 安装记录，本机可能没装 Ubisoft Connect 或没有 Ubisoft 游戏。");
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
            warnings.Add($"Ubisoft Connect：\"{name}\" 的安装目录已不存在（{normalized}），已跳过。");
            return null;
        }

        var exe = GameLibraryUtil.FindMainExecutable(normalized, name, null);
        if (exe.Length == 0)
        {
            warnings.Add($"Ubisoft Connect：\"{name}\"（{normalized}）没能识别出主程序 exe。");
        }

        return new GameInfo
        {
            Name = name,
            Platform = GamePlatform.Ubisoft,
            InstallDir = normalized,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            DiscoverySource = source,
        };
    }
}
