// ============================================================================
//  Gpd.Core / Games / EpicLibraryScanner.cs
//  ---------------------------------------------------------------------------
//  Epic Games 库扫描：读启动器清单目录里的 *.item 清单文件，
//  以及 LauncherInstalled.dat 作为兜底。两者都是纯 JSON，只读。
// ============================================================================

using System.Text.Json;

namespace Gpd.Core.Games;

/// <summary>Epic Games 平台扫描器。</summary>
public sealed class EpicLibraryScanner : IGameLibraryScanner
{
    public string Name => "Epic Games";

    public IEnumerable<GameInfo> Scan(List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var manifestDir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var launcherInstalled = Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var foundAnySource = false;

        if (GameLibraryUtil.DirectoryExists(manifestDir))
        {
            foundAnySource = true;
            string[] files;
            try
            {
                files = Directory.GetFiles(manifestDir, "*.item", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                warnings.Add($"Epic：读取清单目录失败（{ex.GetType().Name}），已跳过。");
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                var game = ParseItemManifest(file, warnings);
                if (game is not null && seen.Add(game.InstallDir))
                {
                    yield return game;
                }
            }
        }

        if (GameLibraryUtil.FileExists(launcherInstalled))
        {
            foundAnySource = true;
            foreach (var game in ParseLauncherInstalled(launcherInstalled, warnings))
            {
                if (seen.Add(game.InstallDir))
                {
                    yield return game;
                }
            }
        }

        if (!foundAnySource)
        {
            warnings.Add("Epic：未检测到 Epic Games 启动器清单（"
                         + $"\"{manifestDir}\" 与 \"{launcherInstalled}\" 都不存在），本机可能没装 Epic 客户端或没有 Epic 游戏。");
        }
    }

    private static GameInfo? ParseItemManifest(string filePath, List<string> warnings)
    {
        JsonDocument document;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            warnings.Add($"Epic：解析 \"{Path.GetFileName(filePath)}\" 失败（{ex.GetType().Name}），已跳过。");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var installLocation = GameLibraryUtil.NormalizePath(GetString(root, "InstallLocation"));
            var appName = GetString(root, "AppName");
            var displayName = GetString(root, "DisplayName");

            if (installLocation.Length == 0)
            {
                return null;
            }

            if (!GameLibraryUtil.DirectoryExists(installLocation))
            {
                warnings.Add($"Epic：清单记录的安装目录不存在（{installLocation}），已跳过。");
                return null;
            }

            // AppCategories 明确不含 "games" 的，是启动器/插件而不是游戏。
            if (root.TryGetProperty("AppCategories", out var categories)
                && categories.ValueKind == JsonValueKind.Array)
            {
                var names = categories.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? string.Empty)
                    .ToList();
                if (names.Count > 0 && !names.Any(n => n.Equals("games", StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }
            }

            // DisplayName 常是本地化键（如 "EpicGames.Fortnite"），不可读时退回 AppName。
            var name = !string.IsNullOrWhiteSpace(displayName) && !displayName.Contains('.', StringComparison.Ordinal)
                ? displayName
                : (string.IsNullOrWhiteSpace(appName) ? Path.GetFileNameWithoutExtension(filePath) : appName);

            var launchExecutable = GetString(root, "LaunchExecutable");
            var exe = string.Empty;
            if (launchExecutable.Length > 0)
            {
                var candidate = GameLibraryUtil.NormalizePath(Path.Combine(installLocation, launchExecutable));
                if (GameLibraryUtil.FileExists(candidate))
                {
                    exe = candidate;
                }
            }

            if (exe.Length == 0)
            {
                exe = GameLibraryUtil.FindMainExecutable(installLocation, name, null);
            }

            var info = new GameInfo
            {
                Name = name,
                Platform = GamePlatform.Epic,
                InstallDir = installLocation,
                ExecutablePath = exe,
                ProcessName = GameLibraryUtil.DeriveProcessName(exe),
                AppId = GetString(root, "CatalogItemId"),
                DiscoverySource = $"Epic 清单 {Path.GetFileName(filePath)}",
            };

            if (long.TryParse(GetString(root, "LastLaunchTime"), out var epoch) && epoch > 0)
            {
                info.LastPlayed = DateTimeOffset.FromUnixTimeSeconds(epoch);
            }

            return info;
        }
    }

    private static IEnumerable<GameInfo> ParseLauncherInstalled(string filePath, List<string> warnings)
    {
        JsonDocument document;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            warnings.Add($"Epic：解析 LauncherInstalled.dat 失败（{ex.GetType().Name}）。");
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("InstallationList", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                warnings.Add("Epic：LauncherInstalled.dat 里没有 InstallationList 数组。");
                yield break;
            }

            foreach (var entry in list.EnumerateArray())
            {
                var installLocation = GameLibraryUtil.NormalizePath(GetString(entry, "InstallLocation"));
                if (installLocation.Length == 0 || !GameLibraryUtil.DirectoryExists(installLocation))
                {
                    continue;
                }

                var name = GetString(entry, "AppName");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = Path.GetFileName(installLocation);
                }

                var exe = GameLibraryUtil.FindMainExecutable(installLocation, name, null);
                yield return new GameInfo
                {
                    Name = name,
                    Platform = GamePlatform.Epic,
                    InstallDir = installLocation,
                    ExecutablePath = exe,
                    ProcessName = GameLibraryUtil.DeriveProcessName(exe),
                    AppId = GetString(entry, "AppName"),
                    DiscoverySource = "Epic LauncherInstalled.dat",
                };
            }
        }
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;
}
