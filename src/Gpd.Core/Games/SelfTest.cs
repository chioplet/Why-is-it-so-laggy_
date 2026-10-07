// ============================================================================
//  Gpd.Core / Games / SelfTest.cs
//  ---------------------------------------------------------------------------
//  真机自测：跑全部平台扫描器 + 配置定位 + 画质解读，并额外做解析器与路径规则的
//  可验证性测试，最后输出一份中文文本报告。不写任何游戏文件（只在 %TEMP% 下
//  造临时样本文件做解析器测试，测完删除）。
//
//  说明：报告中凡是"合成 GameInfo"的部分，都是为了验证路径匹配规则本身；
//  真实游戏的部分全部来自本机实际扫描结果，不作任何伪造。
// ============================================================================

using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Gpd.Core.Games;

/// <summary>自测入口。</summary>
public static class SelfTest
{
    private const int MaxConfigFilesPerGameInReport = 6;
    private const int MaxInsightsInReport = 25;
    private const int MaxWarningsInReport = 40;

    /// <summary>跑一遍完整自测并返回中文文本报告。</summary>
    public static string RunSelfTest()
    {
        var report = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        report.AppendLine("================ Gpd.Core.Games 自测报告 ================");
        report.AppendLine($"运行时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"当前用户：{Environment.UserName}，管理员：{(IsAdministrator() ? "是" : "否")}");
        report.AppendLine($"操作系统：{Environment.OSVersion}，进程 {(Environment.Is64BitProcess ? "64" : "32")} 位");
        report.AppendLine($"工作目录：{AppContext.BaseDirectory}");
        report.AppendLine();

        // ------------------------------------------------------------ 1. 逐个扫描器
        var scannerWarnings = new List<string>();
        var scanners = GameLibraryService.CreateDefaultScanners();
        var perScanner = new List<(string Name, List<GameInfo> Games)>();

        report.AppendLine("【1】平台扫描器逐个运行（真实机器）");
        foreach (var scanner in scanners)
        {
            var localWarnings = new List<string>();
            var games = new List<GameInfo>();
            string? failure = null;

            try
            {
                games.AddRange(scanner.Scan(localWarnings));
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name} {ex.Message}";
            }

            perScanner.Add((scanner.Name, games));
            report.AppendLine($"  · {scanner.Name}：{(failure is null ? games.Count + " 个" : "异常 → " + failure)}");

            foreach (var game in games)
            {
                report.AppendLine($"      - {game.Name}"
                                  + $"｜appid={(game.AppId.Length > 0 ? game.AppId : "-")}"
                                  + $"｜exe={(game.ExecutablePath.Length > 0 ? game.ExecutablePath : "未识别")}"
                                  + $"｜最近游玩={(game.LastPlayed is null ? "-" : game.LastPlayed.Value.ToString("yyyy-MM-dd"))}"
                                  + $"｜时长={(game.PlayTime is null ? "-" : FormatDuration(game.PlayTime.Value))}");
                report.AppendLine($"        安装目录：{game.InstallDir}");
                report.AppendLine($"        识别来源：{game.DiscoverySource}");
            }

            scannerWarnings.AddRange(localWarnings);
        }

        var totalFound = perScanner.Sum(p => p.Games.Count);
        report.AppendLine($"  小计：默认扫描器共识别 {totalFound} 个已安装条目");
        report.AppendLine();

        // ------------------------------------------------ 2. 统一入口 + 配置定位
        report.AppendLine("【2】统一入口 GameLibraryService.ScanAll（去重 + 配置文件定位 + 画质解读）");
        var scanStopwatch = Stopwatch.StartNew();
        var serviceWarnings = new List<string>();
        var merged = GameLibraryService.ScanAll(serviceWarnings, includeStandalone: false, attachConfigFiles: true);
        scanStopwatch.Stop();
        report.AppendLine($"  合并后游戏数：{merged.Count}（扫描耗时 {scanStopwatch.ElapsedMilliseconds} ms）");

        var gamesWithConfig = merged.Count(g => g.ConfigFiles.Count > 0);
        var totalConfigFiles = merged.Sum(g => g.ConfigFiles.Count);
        var totalEntries = merged.Sum(g => g.ConfigFiles.Sum(f => f.Entries.Count));
        var parseErrors = merged.SelectMany(g => g.ConfigFiles).Count(f => f.Error.Length > 0);

        report.AppendLine($"  定位到配置文件的游戏：{gamesWithConfig}/{merged.Count}");
        report.AppendLine($"  配置文件总数：{totalConfigFiles}，解析出的键值总数：{totalEntries}，带错误/说明的文件：{parseErrors}");

        foreach (var game in merged)
        {
            var withExe = game.ExecutablePath.Length > 0 ? "有" : "无";
            var insights = GraphicsSettingsAnalyzer.Analyze(game);
            report.AppendLine($"  · {game.Name}｜平台={game.PlatformName}｜主程序={withExe}"
                              + $"｜配置文件={game.ConfigFiles.Count}个"
                              + $"｜键值={game.ConfigFiles.Sum(f => f.Entries.Count)}条"
                              + $"｜画质结论={insights.Count}条");
        }

        report.AppendLine();

        // ------------------------------------------------------ 3. 配置文件明细
        report.AppendLine("【3】配置文件明细（真实扫描结果，每个游戏最多列 "
                          + MaxConfigFilesPerGameInReport + " 个）");
        foreach (var game in merged)
        {
            report.AppendLine($"  ── {game.Name}（{game.ConfigFiles.Count} 个）");
            if (game.ConfigFiles.Count == 0)
            {
                report.AppendLine("     （没有找到配置文件）");
                continue;
            }

            foreach (var file in game.ConfigFiles.Take(MaxConfigFilesPerGameInReport))
            {
                var state = file.Error.Length > 0 ? $"｜说明={file.Error}" : string.Empty;
                report.AppendLine($"     · [{file.Format}] {file.Path}");
                report.AppendLine($"       用途={file.Purpose}｜体积={file.SizeBytes}B"
                                  + $"｜最后写入={(file.LastWriteTime is null ? "-" : file.LastWriteTime.Value.ToString("yyyy-MM-dd HH:mm"))}"
                                  + $"｜键值={file.Entries.Count}条{state}");
            }

            if (game.ConfigFiles.Count > MaxConfigFilesPerGameInReport)
            {
                report.AppendLine($"     …（另有 {game.ConfigFiles.Count - MaxConfigFilesPerGameInReport} 个文件未列出）");
            }
        }

        report.AppendLine();

        // -------------------------------------------------------- 4. 画质设置解读
        report.AppendLine("【4】画质设置解读（真实配置文件里识别出的结论，按性能影响降序）");
        var allInsights = merged
            .SelectMany(g => GraphicsSettingsAnalyzer.Analyze(g).Select(i => (Game: g.Name, Insight: i)))
            .OrderByDescending(x => x.Insight.PerformanceImpact)
            .ToList();

        report.AppendLine($"  共识别 {allInsights.Count} 条，下面列出前 {Math.Min(MaxInsightsInReport, allInsights.Count)} 条：");
        foreach (var (gameName, insight) in allInsights.Take(MaxInsightsInReport))
        {
            report.AppendLine($"  [影响{insight.PerformanceImpact}] {insight.SettingName}"
                              + $"｜{gameName}｜{insight.SourceKey} = {Truncate(insight.RawValue, 40)}"
                              + $" → {insight.NormalizedValue}");
            report.AppendLine($"      {insight.Comment}");
        }

        if (allInsights.Count == 0)
        {
            report.AppendLine("  （没有识别出任何画质设置：本机配置文件里可能不含常见画质键名）");
        }

        report.AppendLine();

        // -------------------------------------------------- 5. 路径规则合成验证
        report.AppendLine("【5】路径匹配规则验证（合成 GameInfo，仅验证规则本身，不代表真实游戏）");
        AppendSyntheticPathChecks(report);

        // ------------------------------------------------------- 6. 解析器自测
        report.AppendLine("【6】解析器自测（在 %TEMP% 下造临时样本文件，测完删除）");
        AppendParserChecks(report);

        // ---------------------------------------------------------- 7. 警告汇总
        // 【1】逐个扫描、【2】统一入口各自产生一遍同样的警告，合并去重后再报，避免"同一句话出现两次"。
        var allWarnings = scannerWarnings
            .Concat(serviceWarnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        report.AppendLine("【7】诊断与警告");
        report.AppendLine($"  逐扫描器 {scannerWarnings.Count} 条 + 统一入口 {serviceWarnings.Count} 条，去重后 {allWarnings.Count} 条：");
        foreach (var warning in allWarnings.Take(MaxWarningsInReport))
        {
            report.AppendLine($"  · {warning}");
        }

        if (allWarnings.Count > MaxWarningsInReport)
        {
            report.AppendLine($"  …（另有 {allWarnings.Count - MaxWarningsInReport} 条未列出）");
        }

        report.AppendLine();

        // ------------------------------------------------------------- 8. 结论
        report.AppendLine("【8】结论");
        report.AppendLine($"  · 默认平台扫描器识别到 {totalFound} 个条目，去重后 {merged.Count} 个。");
        report.AppendLine($"  · 其中 {gamesWithConfig} 个游戏定位到配置文件，共 {totalConfigFiles} 个文件、{totalEntries} 条键值。");
        report.AppendLine($"  · 画质设置识别 {allInsights.Count} 条。");
        report.AppendLine("  · 没有任何游戏文件被写入或修改（全程 FileAccess.Read 只读打开）。");

        stopwatch.Stop();
        report.AppendLine($"  总耗时：{stopwatch.ElapsedMilliseconds} ms");
        report.AppendLine("================ 报告结束 ================");

        return report.ToString();
    }

    // ---------------------------------------------------------------- 合成路径验证
    private static void AppendSyntheticPathChecks(StringBuilder report)
    {
        // Unity 风格：本机 LocalLow 下确有 Unknown Worlds\Subnautica（但里面只有日志，没有配置文件）。
        var subnautica = new GameInfo { Name = "Subnautica", ProcessName = "Subnautica" };
        var subnauticaDirs = ConfigFileLocator.FindMatchedUserDirectories(subnautica);
        report.AppendLine($"  · 合成 GameInfo(Name=Subnautica)：命中用户目录 {subnauticaDirs.Count} 个");
        foreach (var dir in subnauticaDirs)
        {
            report.AppendLine($"      - {dir.Source} → {dir.Path}");
        }

        var subnauticaCandidates = ConfigFileLocator.FindCandidates(subnautica);
        report.AppendLine($"    候选配置文件 {subnauticaCandidates.Count} 个"
                          + (subnauticaCandidates.Count == 0 ? "（该目录下确实没有设置类文件，只有 Player.log）" : string.Empty));

        // My Games 风格：本机确有 Documents\My Games\Watch_Dogs 2\WD2_GamerProfile.xml。
        var watchDogs = new GameInfo { Name = "Watch_Dogs 2", ProcessName = "WatchDogs2" };
        var watchDogsDirs = ConfigFileLocator.FindMatchedUserDirectories(watchDogs);
        report.AppendLine($"  · 合成 GameInfo(Name=Watch_Dogs 2)：命中用户目录 {watchDogsDirs.Count} 个");
        foreach (var dir in watchDogsDirs)
        {
            report.AppendLine($"      - {dir.Source} → {dir.Path}");
        }

        var watchDogsCandidates = ConfigFileLocator.FindCandidates(watchDogs);
        report.AppendLine($"    候选配置文件 {watchDogsCandidates.Count} 个：");
        foreach (var candidate in watchDogsCandidates.Take(5))
        {
            report.AppendLine($"      - [{candidate.Priority}] {candidate.Path}（用途：{candidate.Purpose}）");
        }

        var xmlParser = new XmlConfigParser();
        var parsedXml = watchDogsCandidates
            .Where(c => xmlParser.CanParse(c.Path))
            .Select(c => xmlParser.Parse(c.Path))
            .FirstOrDefault(f => f.Entries.Count > 0);
        if (parsedXml is not null)
        {
            report.AppendLine($"    XML 解析：{parsedXml.Path} → {parsedXml.Entries.Count} 条键值，前 3 条：");
            foreach (var entry in parsedXml.Entries.Take(3))
            {
                report.AppendLine($"      {entry.Section} / {entry.Key} = {Truncate(entry.Value, 50)}");
            }
        }
        else
        {
            report.AppendLine("    XML 解析：没有可解析的样例文件。");
        }

        // 反向验证：短词元（cs2 / 2）不能误伤。
        var cs2 = new GameInfo { Name = "Counter-Strike 2", ProcessName = "cs2" };
        var cs2Dirs = ConfigFileLocator.FindMatchedUserDirectories(cs2);
        var falsePositive = cs2Dirs.Any(d => d.Path.Contains("Watch_Dogs", StringComparison.OrdinalIgnoreCase));
        report.AppendLine($"  · 反向验证：合成 GameInfo(Name=Counter-Strike 2) 命中 {cs2Dirs.Count} 个目录，"
                          + $"其中是否误伤 Watch_Dogs 2 → {(falsePositive ? "是（bug）" : "否")}");
        foreach (var dir in cs2Dirs.Take(5))
        {
            report.AppendLine($"      - {dir.Source} → {dir.Path}");
        }

        // Apex：真实游戏，配置在 Saved Games\Respawn\Apex（多一层发行商目录）。
        var apex = new GameInfo { Name = "Apex Legends", ProcessName = "r5apex_dx12" };
        var apexDirs = ConfigFileLocator.FindMatchedUserDirectories(apex);
        report.AppendLine($"  · 合成 GameInfo(Name=Apex Legends)：命中用户目录 {apexDirs.Count} 个（验证 Saved Games\\<发行商>\\<游戏> 两层匹配）");
        foreach (var dir in apexDirs.Take(5))
        {
            report.AppendLine($"      - {dir.Source} → {dir.Path}");
        }

        // 噪声过滤：本地化/日志/素材目录里的 ini 不能当画质配置
        // （真实机器上 OBS 的 data\obs-studio\locale 与 MyDockFinder 的 lang 都踩过这个坑）。
        var noiseRoot = Path.Combine(Path.GetTempPath(), "GpdCoreNoiseFilter");
        try
        {
            Directory.CreateDirectory(Path.Combine(noiseRoot, "lang"));
            Directory.CreateDirectory(Path.Combine(noiseRoot, "data", "obs-studio", "locale"));
            Directory.CreateDirectory(Path.Combine(noiseRoot, "logs"));
            Directory.CreateDirectory(Path.Combine(noiseRoot, "assets", "materials"));

            File.WriteAllText(Path.Combine(noiseRoot, "config.ini"),
                "[Graphics]\r\nShadowQuality=3\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(noiseRoot, "locale.ini"),
                "Fullscreen=Fullscreen\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(noiseRoot, "lang", "Vietnamese.ini"),
                "editicon/iconshadow=Bong bóng biểu tượng\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(noiseRoot, "data", "obs-studio", "locale", "ar-SA.ini"),
                "EffectFilters=Parzûnên bandorê\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(noiseRoot, "logs", "boot.ini"),
                "Resolution=1920\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(noiseRoot, "assets", "materials", "combine_video_hdr.json"),
                "{\"passes\":[{\"textures\":[\"_rt_FullFrameBuffer\"]}]}", new UTF8Encoding(false));

            var noiseGame = new GameInfo { Name = "GpdNoiseFilterProbe", InstallDir = noiseRoot };
            var noiseCandidates = ConfigFileLocator.FindCandidates(noiseGame)
                .Where(c => c.Path.StartsWith(noiseRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            report.AppendLine($"  · 噪声过滤：合成目录里放了 6 个长得像配置的文件，实际收录 {noiseCandidates.Count} 个");
            foreach (var candidate in noiseCandidates)
            {
                report.AppendLine($"      - 收录 {Path.GetRelativePath(noiseRoot, candidate.Path)}（用途：{candidate.Purpose}）");
            }

            var kept = noiseCandidates
                .Select(c => Path.GetRelativePath(noiseRoot, c.Path))
                .ToList();
            var configKept = kept.Any(k => k.Equals("config.ini", StringComparison.OrdinalIgnoreCase));
            var leaked = kept.Where(k => !k.Equals("config.ini", StringComparison.OrdinalIgnoreCase)).ToList();
            report.AppendLine($"      判定：config.ini {(configKept ? "已收录 ✓" : "未收录（bug）")}；"
                              + $"本地化/日志/素材文件泄漏 {leaked.Count} 个"
                              + $"{(leaked.Count == 0 ? " ✓" : "（bug）")}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  · 噪声过滤自测执行失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(noiseRoot))
                {
                    Directory.Delete(noiseRoot, true);
                }
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响结论。
            }
        }

        report.AppendLine();
    }

    // ------------------------------------------------------------------ 解析器自测
    private static void AppendParserChecks(StringBuilder report)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GpdCoreSelfTest");
        try
        {
            Directory.CreateDirectory(tempDir);
        }
        catch (Exception ex)
        {
            report.AppendLine($"  （无法创建临时目录 {tempDir}：{ex.GetType().Name}，跳过解析器自测）");
            return;
        }

        try
        {
            // --- ini：三种写法 + 节 + 注释
            var iniPath = Path.Combine(tempDir, "sample.ini");
            File.WriteAllText(iniPath, string.Join("\r\n",
                "; 注释行",
                "# 另一种注释",
                "// Source 引擎风格注释",
                string.Empty,
                "[Graphics]",
                "ResolutionX=1920",
                "ResolutionY=1080",
                "ShadowQuality=3",
                "MotionBlur = 0",
                "sg.ViewDistanceQuality=4",
                string.Empty,
                "[Source]",
                "r_drawtracers \"1\"",
                "fps_max 300",
                "quit"), new UTF8Encoding(false));

            var iniParser = new IniConfigParser();
            var ini = iniParser.Parse(iniPath);
            report.AppendLine($"  · INI/CFG 解析：{Path.GetFileName(iniPath)} → {ini.Entries.Count} 条"
                              + "（期望 8 条：等号 5 + 空格 2 + 裸命令 1）");
            foreach (var entry in ini.Entries)
            {
                report.AppendLine($"      [{entry.Section}] {entry.Key} = {entry.Value}");
            }

            // --- json：嵌套对象 + 数组
            var jsonPath = Path.Combine(tempDir, "sample.json");
            File.WriteAllText(jsonPath, """
            {
              "Graphics": {
                "Resolution": { "Width": 2560, "Height": 1440 },
                "Quality": "High",
                "VSync": false,
                "Upscalers": ["DLSS", "FSR"]
              },
              "EnableRayTracing": true
            }
            """, new UTF8Encoding(false));

            var jsonParser = new JsonConfigParser();
            var json = jsonParser.Parse(jsonPath);
            report.AppendLine($"  · JSON 解析：{Path.GetFileName(jsonPath)} → {json.Entries.Count} 条");
            foreach (var entry in json.Entries)
            {
                report.AppendLine($"      [{entry.Section}] {entry.Key} = {entry.Value}");
            }

            // --- xml：属性 + 嵌套
            var xmlPath = Path.Combine(tempDir, "sample.xml");
            File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <GamerProfile>
              <Video Quality="Ultra">
                <Resolution Width="1920" Height="1080" />
                <VSync>0</VSync>
              </Video>
              <Audio><Master>80</Master></Audio>
            </GamerProfile>
            """, new UTF8Encoding(false));

            var xmlParser = new XmlConfigParser();
            var xml = xmlParser.Parse(xmlPath);
            report.AppendLine($"  · XML 解析：{Path.GetFileName(xmlPath)} → {xml.Entries.Count} 条"
                              + "（期望 5 条：@Quality + @Width + @Height + VSync + Master；"
                              + "自闭合的 <Resolution> 只算 2 条属性，不再多补一条空文本）");
            foreach (var entry in xml.Entries)
            {
                report.AppendLine($"      [{entry.Section}] {entry.Key} = {entry.Value}");
            }

            // --- 体积保护：造一个 9MB 的 ini
            var bigPath = Path.Combine(tempDir, "big.ini");
            var line = "SomeSetting=1\r\n";
            using (var writer = new StreamWriter(bigPath, append: false, new UTF8Encoding(false)))
            {
                var target = 9L * 1024 * 1024;
                var written = 0L;
                while (written < target)
                {
                    writer.Write(line);
                    written += line.Length;
                }
            }

            var big = ConfigFileReader.Read(bigPath, "体积保护测试");
            report.AppendLine($"  · 体积保护：{Path.GetFileName(bigPath)} 实际 {new FileInfo(bigPath).Length} 字节"
                              + $" → 条目 {big.Entries.Count} 条，说明=\"{big.Error}\""
                              + $"（期望：不解析内容并给出超限说明）");

            // --- 解析器分发与格式识别
            report.AppendLine("  · 解析器分发："
                              + $".ini→{ConfigFileReader.DetectFormat("a.ini")}"
                              + $"，.cfg→{ConfigFileReader.DetectFormat("a.cfg")}"
                              + $"，.json→{ConfigFileReader.DetectFormat("a.json")}"
                              + $"，.xml→{ConfigFileReader.DetectFormat("a.xml")}"
                              + $"，.bin→{ConfigFileReader.DetectFormat("a.bin")}");

            // --- 画质解读（用前面 ini 的内容）
            var insights = GraphicsSettingsAnalyzer.Analyze(ini);
            report.AppendLine($"  · 画质解读（对 sample.ini）：{insights.Count} 条");
            foreach (var insight in insights)
            {
                report.AppendLine($"      [影响{insight.PerformanceImpact}] {insight.SettingName}"
                                  + $"  {insight.SourceKey} = {insight.RawValue} → {insight.NormalizedValue}");
            }

            // --- 抗噪：本地化文本、空值、JSON 数组下标都不能产出画质结论
            var noisy = new List<ConfigEntry>
            {
                new() { Section = "Basic.Filters", Key = "EffectFilters", Value = "Parzûnên bandorê" },
                new() { Section = "Basic.Settings.Stream", Key = "ResetOSXVSyncOnExit",
                        Value = "macOS V-Sync ji nû ve saz bike li ser derketinê" },
                new() { Section = "editicon", Key = "iconshadow", Value = "Bong bóng biểu tượng" },
                new() { Section = "", Key = "particle_asset", Value = string.Empty },
                new() { Section = "passes[0]", Key = "textures[0]", Value = "_rt_FullFrameBuffer" },
                new() { Section = "Graphics", Key = "ShadowQuality", Value = "Ultra" },
                new() { Section = "Graphics", Key = "VSync", Value = "Windowed Fullscreen" },
            };

            var noisyInsights = GraphicsSettingsAnalyzer.Analyze(noisy);
            report.AppendLine($"  · 抗噪自测：7 条输入（5 条噪声 + 2 条真设置）→ 产出 {noisyInsights.Count} 条结论"
                              + $"（期望 2 条：阴影质量=Ultra、垂直同步=Windowed Fullscreen）");
            foreach (var insight in noisyInsights)
            {
                report.AppendLine($"      [影响{insight.PerformanceImpact}] {insight.SettingName}"
                                  + $"  {insight.SourceKey} = {insight.RawValue} → {insight.NormalizedValue}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  （解析器自测异常：{ex.GetType().Name} {ex.Message}）");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
                report.AppendLine($"  （临时目录已清理：{tempDir}）");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  （临时目录清理失败：{ex.GetType().Name}，位置 {tempDir}）");
            }
        }

        report.AppendLine();
    }

    // -------------------------------------------------------------------- 小工具
    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{duration.TotalHours:F1} 小时"
            : $"{duration.TotalMinutes:F0} 分钟";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
