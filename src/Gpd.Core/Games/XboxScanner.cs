// ============================================================================
//  Gpd.Core / Games / XboxScanner.cs
//  ---------------------------------------------------------------------------
//  Xbox / Microsoft Store 库扫描。这类游戏装在受保护目录里，没有常规清单文件，
//  所以用三条可枚举的线索：
//    1) 各个固定磁盘根下的 <盘>:\XboxGames\<游戏>  （Xbox App 默认安装位置）
//    2) %ProgramFiles%\ModifiableWindowsApps\<游戏> （支持 mod 的 Xbox 游戏）
//    3) 卸载项里 InstallLocation 落在 \WindowsApps\ 下的条目
//  WindowsApps 本身通常无读取权限，这里只把它当线索，不强行访问。只读。
// ============================================================================

using Microsoft.Win32;

namespace Gpd.Core.Games;

/// <summary>Xbox / Microsoft Store 平台扫描器。</summary>
public sealed class XboxScanner : IGameLibraryScanner
{
    public string Name => "Xbox";

    private static readonly string[] CommonRootNames = { "XboxGames", "ModifiableWindowsApps" };

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawRoot = false;

        // 线索 1 + 2：固定磁盘 / Program Files 下的游戏根目录。
        var roots = new List<string>();
        foreach (var drive in SafeGetReadyFixedDrives())
        {
            foreach (var rootName in CommonRootNames)
            {
                roots.Add(Path.Combine(drive, rootName));
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length > 0)
        {
            roots.Add(Path.Combine(programFiles, "ModifiableWindowsApps"));
        }

        foreach (var root in roots)
        {
            if (!GameLibraryUtil.DirectoryExists(root))
            {
                continue;
            }

            sawRoot = true;
            string[] children;
            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception ex)
            {
                warnings.Add($"Xbox：无法枚举 \"{root}\"（{ex.GetType().Name}），需要管理员权限才能扫描。");
                continue;
            }

            foreach (var child in children)
            {
                // "." 开头的是暂存目录，不是已安装游戏。
                var leaf = Path.GetFileName(child);
                if (leaf.StartsWith('.') || leaf.Equals("Deleted", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var game = Build(leaf, child, $"目录 {child}", warnings);
                if (game is not null && seen.Add(game.InstallDir))
                {
                    yield return game;
                }
            }
        }

        // 线索 3：卸载项里指向 WindowsApps 的条目。
        foreach (var entry in GameLibraryUtil.UninstallEntries)
        {
            var installDir = GameLibraryUtil.TryDeriveInstallDir(entry);
            var isWindowsApps = installDir.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                                || entry.DisplayIcon.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                                || entry.UninstallString.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
            if (!isWindowsApps)
            {
                continue;
            }

            sawRoot = true;
            var game = Build(entry.DisplayName, installDir, $"卸载项 {entry.RegistryPath}", warnings);
            if (game is not null && seen.Add(game.InstallDir))
            {
                yield return game;
            }
        }

        if (seen.Count == 0)
        {
            warnings.Add(sawRoot
                ? "Xbox：找到 XboxGames / WindowsApps 相关线索，但没有可读取的游戏目录（受保护目录需要管理员权限），本机未识别到 Xbox 游戏。"
                : "Xbox：未检测到 XboxGames / ModifiableWindowsApps / WindowsApps 安装目录，本机没有已安装的 Xbox 游戏。");
        }
    }

    private static GameInfo? Build(string name, string installDir, string source, List<string> warnings)
    {
        var normalized = GameLibraryUtil.NormalizePath(installDir);
        if (normalized.Length == 0 || !GameLibraryUtil.DirectoryExists(normalized))
        {
            return null;
        }

        var exe = GameLibraryUtil.FindMainExecutable(normalized, name, null);
        if (exe.Length == 0)
        {
            // Xbox 游戏的 exe 常在受保护的 WindowsApps 深层，这里如实提示而不是伪造路径。
            warnings.Add($"Xbox：\"{name}\"（{normalized}）没能识别出主程序 exe，可能需要管理员权限。");
        }

        return new GameInfo
        {
            Name = name,
            Platform = GamePlatform.Xbox,
            InstallDir = normalized,
            ExecutablePath = exe,
            ProcessName = GameLibraryUtil.DeriveProcessName(exe),
            DiscoverySource = source,
        };
    }

    /// <summary>列出就绪的固定磁盘（不抛异常）。</summary>
    private static IEnumerable<string> SafeGetReadyFixedDrives()
    {
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
            try
            {
                usable = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (Exception)
            {
                usable = false;
            }

            if (usable)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }
}
