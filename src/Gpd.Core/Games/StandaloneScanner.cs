// ============================================================================
//  Gpd.Core / Games / StandaloneScanner.cs
//  ---------------------------------------------------------------------------
//  "独立安装"游戏扫描：没有平台管理器，只能靠系统卸载项 + 安装目录特征推断。
//  误报风险天然较高，所以 **默认不启用**，由调用方显式打开
//  （GameLibraryService.ScanAll(warnings, includeStandalone: true)）。
//  只读注册表与目录，绝不写入。
// ============================================================================

namespace Gpd.Core.Games;

/// <summary>独立安装（无平台管理器）游戏扫描器。默认不参与扫描。</summary>
public sealed class StandaloneScanner : IGameLibraryScanner
{
    public string Name => "独立安装";

    /// <summary>这些发行商/名字是工具、运行时或平台客户端，不是游戏。</summary>
    private static readonly string[] NonGameFragments =
    {
        "microsoft visual c++", "visual studio", "dotnet", ".net ", "windows sdk", "windows driver",
        "update for", "hotfix", "redistributable", "runtime", "python", "node.js", "java ",
        "nvidia", "amd ", "intel ", "realtek", "adobe", "google", "mozilla", "7-zip", "winrar",
        "git ", "docker", "jetbrains", "office", "edge", "onedrive", "steam", "epic games",
        "battle.net", "gog galaxy", "ubisoft connect", "wegame", "ea app", "origin",
        "antivirus", "driver", "printer", "synology", "itunes", "zoom", "teams", "slack",
        "obs studio", "wallpaper engine", "mydockfinder", "vulkan", "directx", "opengl",
    };

    /// <summary>出现这些目录名，基本可以确认是游戏安装（引擎/资源结构）。</summary>
    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var considered = 0;
        var rejected = 0;

        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            var haystack = (entry.DisplayName + "|" + entry.Publisher).ToLowerInvariant();
            if (NonGameFragments.Any(f => haystack.Contains(f, StringComparison.Ordinal)))
            {
                continue;
            }

            var installDir = GameLibraryUtil.TryDeriveInstallDir(entry);
            if (installDir.Length == 0 || !GameLibraryUtil.DirectoryExists(installDir))
            {
                continue;
            }

            considered++;
            var notes = new List<string>();
            var exe = GameLibraryUtil.FindMainExecutable(installDir, entry.DisplayName, notes);
            if (exe.Length == 0)
            {
                rejected++;
                continue;
            }

            if (!GameLibraryUtil.LooksLikeGameInstall(installDir, exe))
            {
                rejected++;
                continue;
            }

            if (!seen.Add(installDir))
            {
                continue;
            }

            yield return new GameInfo
            {
                Name = entry.DisplayName,
                Platform = GamePlatform.Standalone,
                InstallDir = installDir,
                ExecutablePath = exe,
                ProcessName = GameLibraryUtil.DeriveProcessName(exe),
                AppId = string.Empty,
                DiscoverySource = $"卸载项 {entry.RegistryPath}",
            };
        }

        warnings.Add($"独立安装：检查了 {considered} 个疑似条目，其中 {rejected} 个因缺少可用主程序或缺少游戏目录特征被排除，"
                     + $"最终识别 {seen.Count} 个。该扫描器默认关闭，结果仅供参考。");
    }
}
