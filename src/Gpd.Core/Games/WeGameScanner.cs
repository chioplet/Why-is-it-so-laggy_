// ============================================================================
//  Gpd.Core / Games / WeGameScanner.cs
//  ---------------------------------------------------------------------------
//  WeGame（腾讯）库扫描：安装信息写在
//  HKLM\SOFTWARE\WOW6432Node\Tencent\WeGame\<游戏名>\InstallPath 一类的位置，
//  另外用卸载项里 Publisher 含 Tencent / 腾讯 的条目兜底。只读注册表。
//  注意：WeGame 客户端自身也在 Tencent 键下，必须排除掉，不能当成游戏。
// ============================================================================

using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>WeGame 平台扫描器。</summary>
public sealed class WeGameScanner : IGameLibraryScanner
{
    public string Name => "WeGame";

    /// <summary>客户端/运行时自身的子键名，不是游戏。</summary>
    private static readonly string[] ClientSubKeys =
    {
        "WeGame", "wegame", "tgp", "TGP", "WeGameLauncher", "rail_files", "rail_sdk",
        "WeGameSetup", "TencentDL", "TenioDL", "QQ", "TXGameAssistant", "WGCommonRedist",
    };

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawRoot = false;

        var locations = new (RegistryHive Hive, RegistryView View, string SubKeyPath)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Tencent\WeGame"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Tencent\WeGame"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Tencent\tgp"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Tencent\tgp"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Tencent\WeGame\Games"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Tencent\WeGame\Games"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, @"SOFTWARE\Tencent\WeGame"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"SOFTWARE\Tencent\WeGame"),
        };

        foreach (var (hive, view, subKeyPath) in locations)
        {
            foreach (var (childName, keyPath, _, value) in GameLibraryUtil.ReadRegistryChildren(
                         hive, view, subKeyPath, "InstallPath", "InstallDir", "GamePath", "Path", "path"))
            {
                if (subKeyPath.EndsWith(@"\Games", StringComparison.OrdinalIgnoreCase) is false
                    && ClientSubKeys.Contains(childName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                sawRoot = true;
                var game = Build(childName, value, $"注册表 {keyPath}", warnings);
                if (game is not null && seen.Add(game.InstallDir))
                {
                    yield return game;
                }
            }
        }

        var filteredNonGames = 0;
        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            if (!entry.Publisher.Contains("Tencent", StringComparison.OrdinalIgnoreCase)
                && !entry.Publisher.Contains("腾讯", StringComparison.Ordinal)
                && !entry.DisplayName.Contains("WeGame", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sawRoot = true;

            // WeGame 客户端本体也会出现在卸载项里，它不是游戏。
            if (GameLibraryUtil.IsPlatformClientName(entry.DisplayName))
            {
                continue;
            }

            var installDir = GameLibraryUtil.TryDeriveInstallDir(entry);
            var exe = installDir.Length > 0
                ? GameLibraryUtil.FindMainExecutable(installDir, entry.DisplayName, null)
                : string.Empty;

            // 腾讯同时发行 QQ / 微信 / 微信输入法 / 各种启动器，只看 Publisher 会把它们全当成游戏
            // （本机实测 6 个候选里 5 个是误报）。要求目录结构或主程序路径真的像游戏才收。
            if (installDir.Length == 0
                || !GameLibraryUtil.DirectoryExists(installDir)
                || !GameLibraryUtil.LooksLikeGameInstall(installDir, exe))
            {
                filteredNonGames++;
                continue;
            }

            var game = Build(entry.DisplayName, installDir, $"卸载项 {entry.RegistryPath}", warnings, exe);
            if (game is not null && seen.Add(game.InstallDir))
            {
                yield return game;
            }
        }

        if (filteredNonGames > 0)
        {
            warnings.Add($"WeGame：有 {filteredNonGames} 个腾讯发布的条目（QQ/微信/输入法/启动器一类）"
                         + "因为安装目录不具备游戏结构而被排除。");
        }

        if (seen.Count == 0)
        {
            warnings.Add(sawRoot
                ? "WeGame：找到腾讯相关注册表记录，但没有可用的游戏安装目录，本机没有已安装的 WeGame 游戏。"
                : "WeGame：未检测到 WeGame 安装记录，本机可能没装 WeGame 或没有 WeGame 游戏。");
        }
    }

    private static GameInfo? Build(string name, string installDir, string source, List<string> warnings, string? exe = null)
    {
        var normalized = GameLibraryUtil.NormalizePath(installDir);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (!GameLibraryUtil.DirectoryExists(normalized))
        {
            warnings.Add($"WeGame：\"{name}\" 的安装目录已不存在（{normalized}），已跳过。");
            return null;
        }

        exe ??= GameLibraryUtil.FindMainExecutable(normalized, name, null);
        if (exe.Length == 0)
        {
            warnings.Add($"WeGame：\"{name}\"（{normalized}）没能识别出主程序 exe。");
        }

        return new GameInfo
        {
            Name = name,
            Platform = GamePlatform.WeGame,
            InstallDir = normalized,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            DiscoverySource = source,
        };
    }
}
