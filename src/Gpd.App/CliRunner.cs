using System.Diagnostics;
using System.Globalization;
using System.IO;
using Gpd.Core;
using Gpd.Core.Collect;
using Gpd.Core.Games;
using Gpd.Core.Reporting;

namespace Gpd.App;

/// <summary>
/// 无界面模式：一次「采集 → 分析 → 出报告」跑完就退出。
/// 存在的意义有两个：方便脚本化（例如每次打游戏都固定跑 60 秒留档），以及让发布出来的 exe 能被自动化端到端验证。
/// </summary>
/// <remarks>
/// 输出走 stdout。本程序是 WinExe（GUI 子系统），只有在调用方重定向了 stdout 时才能看到输出；
/// 直接在资源管理器里双击不会弹控制台，属正常现象。
/// </remarks>
internal static class CliRunner
{
    private sealed class Options
    {
        public bool Help;
        public int? Pid;
        public string? ProcessName;
        public string? GameName;
        public double Interval = 1.0;
        public double? Seconds;
        public string OutputDirectory = "";
        public string Formats = "";
        public bool Fps;
        public bool ScanGames;
        public bool StopOnExit = true;
    }

    public static int Run(string[] args)
    {
        ConfigureOutputEncoding();

        var opts = Parse(args);
        if (opts.Help)
        {
            PrintUsage();
            return 0;
        }

        // ── 1. 确定目标进程 ───────────────────────────────────────────────
        int pid;
        string processName;
        if (opts.Pid is { } explicitPid)
        {
            pid = explicitPid;
            processName = TryGetProcessName(pid) ?? $"pid{pid}";
        }
        else if (!string.IsNullOrWhiteSpace(opts.ProcessName))
        {
            var found = FindByName(opts.ProcessName);
            if (found is null)
            {
                Console.WriteLine($"没有找到名为 {opts.ProcessName} 的运行中进程。");
                return 2;
            }
            (pid, processName) = found.Value;
        }
        else
        {
            Console.WriteLine("必须指定 --pid <PID> 或 --process <进程名>。用 --help 看用法。");
            return 2;
        }

        var outputDir = string.IsNullOrWhiteSpace(opts.OutputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "游戏性能诊断报告")
            : opts.OutputDirectory;

        Console.WriteLine($"目标进程：{processName} (PID {pid})");
        Console.WriteLine($"输出目录：{outputDir}");
        Console.WriteLine($"采样间隔：{opts.Interval}s　时长：{(opts.Seconds is { } s ? $"{s}s" : "直到进程退出")}　帧率模块：{(opts.Fps ? "开" : "关")}");
        Console.Out.Flush();

        // ── 2. 关联游戏库（可选） ─────────────────────────────────────────
        GameInfo? game = null;
        if (opts.ScanGames || !string.IsNullOrWhiteSpace(opts.GameName))
        {
            var warnings = new List<string>();
            var games = GameLibraryService.ScanAll(warnings, includeStandalone: false, attachConfigFiles: true);
            Console.WriteLine($"游戏库扫描：识别到 {games.Count} 个游戏，{games.Count(g => g.ConfigFiles.Count > 0)} 个含配置文件。");
            foreach (var w in warnings.Distinct().Take(8)) Console.WriteLine($"  扫描说明：{w}");

            // 不打印清单的话，用户根本无从知道该给 --game 传什么名字。
            if (games.Count > 0)
            {
                Console.WriteLine("  识别到的游戏（可直接用 --game \"名字\" 指定）：");
                foreach (var g in games.Take(30))
                {
                    Console.WriteLine($"    · {g.Name}　[{g.PlatformName}]　进程 {g.ProcessName ?? "未知"}"
                        + $"　配置文件 {g.ConfigFiles.Count} 个");
                }
                if (games.Count > 30) Console.WriteLine($"    …… 另有 {games.Count - 30} 个未列出");
            }

            game = !string.IsNullOrWhiteSpace(opts.GameName)
                ? games.FirstOrDefault(g => g.Name.Contains(opts.GameName, StringComparison.OrdinalIgnoreCase))
                : games.FirstOrDefault(g => string.Equals(g.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine(game is null
                ? "  未匹配到游戏库条目：报告只含系统级结论，没有画质配置解读。"
                : $"  已匹配：{game.Name}（{game.PlatformName}），配置文件 {game.ConfigFiles.Count} 个。");
            Console.Out.Flush();
        }

        // ── 3. 采样 ──────────────────────────────────────────────────────
        using var sampler = new PerformanceSampler(opts.Fps);

        Console.WriteLine("── 采集能力 ──");
        foreach (var st in sampler.CollectorStatuses)
            Console.WriteLine($"[{(st.Available ? "可用" : "不可用")}] {st.Name}：{st.Detail}");
        Console.Out.Flush();

        sampler.ProgressChanged += (_, e) =>
        {
            var sm = e.Sample;
            Console.WriteLine(
                $"  {e.ElapsedSeconds,6:F1}s | CPU {Fmt(sm.CpuTotalPercent, "%")} {Fmt(sm.CpuFrequencyMhz, "MHz", 0)} {Fmt(sm.CpuPackageTempC, "C")} {Fmt(sm.CpuPackagePowerW, "W")}"
                + $" | GPU {Fmt(sm.GpuUtilPercent, "%")} {Fmt(sm.GpuClockMhz, "MHz", 0)} {Fmt(sm.GpuTempC, "C")} {Fmt(sm.GpuPowerW, "W")}"
                + $" | MEM {Fmt(sm.ProcessWorkingSetMb, "MB", 0)} | 系统可用 {Fmt(sm.SystemAvailableMemoryMb, "MB", 0)}"
                + (sm.Fps.HasValue ? $" | {sm.Fps.Value:F1} FPS" : ""));
        };

        sampler.Start(new SamplingOptions
        {
            ProcessId = pid,
            ProcessName = processName,
            IntervalSeconds = opts.Interval,
            DurationSeconds = opts.Seconds,
            StopWhenProcessExits = opts.StopOnExit,
            EnableFps = opts.Fps,
        });
        if (sampler.CurrentSession is { } live) live.Game = game;

        sampler.WaitForCompletionAsync().GetAwaiter().GetResult();
        var session = sampler.CurrentSession;
        if (session is null || session.Samples.Count == 0)
        {
            Console.WriteLine("没有采到任何样本，放弃生成报告。");
            return 3;
        }

        var actualSeconds = (session.EndedAt - session.StartedAt).TotalSeconds;
        Console.WriteLine($"采样结束：{session.Samples.Count} 个样本 / {actualSeconds:F1} 秒 / 停止原因 {session.StopReason}");
        foreach (var w in session.Warnings.Distinct()) Console.WriteLine($"  [告警] {w}");
        Console.Out.Flush();

        // ── 4. 分析 + 出报告 ─────────────────────────────────────────────
        var report = ReportBuilder.Build(session, game);

        var messages = new List<string>();
        var failedFormats = new List<ReportFormat>();
        var written = ReportBuilder.WriteAll(report, outputDir, ReportBuilder.ParseFormats(opts.Formats), messages, failedFormats);

        Console.WriteLine("── 结论 ──");
        foreach (var f in report.Findings)
            Console.WriteLine($"[{f.Severity}] {f.Title}");

        var s2 = report.Summary;
        Console.WriteLine("── 摘要 ──");
        Console.WriteLine($"  样本 {s2.SampleCount} 个 / {s2.DurationSeconds:F1} 秒，瓶颈判定：{s2.BottleneckVerdict}");
        // 只有当温度确实随负载变化、能当 CPU 封装温度用时才敢写"温度"；
        // 否则必须写"热区温度"，免得控制台这一行把主板 ACPI 热区读数冒充成 CPU 温度。
        var cpuTempLabel = s2.CpuTempTracksLoad == true ? "温度" : "热区温度";
        Console.WriteLine($"  CPU 平均/最高占用 {Fmt(s2.CpuAvgPercent, "%")} / {Fmt(s2.CpuMaxPercent, "%")}，"
                        + $"频率 平均 {Fmt(s2.CpuAvgFrequencyMhz, "MHz", 0)}，{cpuTempLabel} 最高 {Fmt(s2.CpuMaxTempC, "C")}，功耗 平均 {Fmt(s2.CpuAvgPowerW, "W")}");
        Console.WriteLine($"  GPU 平均/最高占用 {Fmt(s2.GpuAvgUtilPercent, "%")} / {Fmt(s2.GpuMaxUtilPercent, "%")}，"
                        + $"频率 平均 {Fmt(s2.GpuAvgClockMhz, "MHz", 0)}，温度 最高 {Fmt(s2.GpuMaxTempC, "C")}，功耗 平均 {Fmt(s2.GpuAvgPowerW, "W")}");
        Console.WriteLine($"  进程内存 平均/最高 {Fmt(s2.ProcessAvgWorkingSetMb, "MB", 0)} / {Fmt(s2.ProcessMaxWorkingSetMb, "MB", 0)}，"
                        + $"系统最低可用 {Fmt(s2.SystemMinAvailableMemoryMb, "MB", 0)}，磁盘 平均/最高 {Fmt(s2.DiskAvgPercent, "%")} / {Fmt(s2.DiskMaxPercent, "%")}");
        if (s2.FpsAvg is { } fpsAvg)
            Console.WriteLine($"  帧率 平均 {fpsAvg:F1}，最低 {Fmt(s2.FpsMin, "", 1)}，1% Low {Fmt(s2.Fps1PercentLow, "", 1)}，卡顿 {s2.StutterCount} 次");

        foreach (var m in messages) Console.WriteLine(m);

        Console.WriteLine("── 输出文件 ──");
        foreach (var (format, path) in written)
        {
            var size = TrySize(path);
            Console.WriteLine($"[{ReportBuilder.FormatName(format)}] {path}（{size}）");
        }
        Console.Out.Flush();

        if (written.Count == 0)
        {
            Console.WriteLine("没有写出任何报告文件。");
            return 4;
        }

        // 请求了多种格式却只成功了一部分：必须让脚本能察觉，否则"要了 PDF 只拿到 Markdown"会被当成成功。
        if (failedFormats.Count > 0)
        {
            Console.WriteLine($"以下格式生成失败：{string.Join("、", failedFormats.Select(ReportBuilder.FormatName))}");
            return 5;
        }

        return 0;
    }

    // ========================================================================

    /// <summary>
    /// 输出编码。实测（本机中文 Windows）：stdout 被重定向成管道/文件时，.NET 仍按系统 OEM 代码页（936）
    /// 写字节，调用方按 UTF-8 读就整片乱码（"游戏性能诊断器" → "��Ϸ���������"）。
    /// 所以重定向时强制 UTF-8；直接挂在控制台上时保持系统代码页，这样控制台里显示才正常。
    /// </summary>
    private static void ConfigureOutputEncoding()
    {
        try
        {
            if (Console.IsOutputRedirected)
                Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // 没有可用的控制台句柄时忽略：输出编码不影响采集与报告本身。
        }
    }

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "--cli":
                    // App.OnStartup 就是靠它分流到这里的，不是给解析器看的参数。
                    break;
                case "--help" or "-h" or "/?":
                    o.Help = true;
                    break;
                case "--pid":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) o.Pid = pid;
                    break;
                case "--process" or "--name":
                    o.ProcessName = Next();
                    break;
                case "--game":
                    o.GameName = Next();
                    break;
                case "--interval":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var iv) && iv >= 0.2) o.Interval = iv;
                    break;
                case "--seconds" or "--duration":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0) o.Seconds = sec;
                    break;
                case "--out" or "--output":
                    o.OutputDirectory = Next() ?? "";
                    break;
                case "--formats":
                    o.Formats = Next() ?? "";
                    break;
                case "--fps":
                    o.Fps = true;
                    break;
                case "--scan-games":
                    o.ScanGames = true;
                    break;
                case "--no-stop-on-exit":
                    o.StopOnExit = false;
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        Console.WriteLine($"（忽略无法识别的参数：{a}）");
                    break;
            }
        }
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            游戏性能诊断器 wiisl - 无界面模式

            用法：
              wiisl.exe --cli (--pid <PID> | --process <进程名>) [选项]

            选项：
              --interval <秒>     采样间隔，默认 1.0（最小 0.2）
              --seconds  <秒>     采集时长；不写表示一直采到目标进程退出
              --out      <目录>   报告输出目录，默认「我的文档\游戏性能诊断报告」
              --formats  <列表>   md,pdf,csv,json 的任意组合，默认全部
              --fps               启用帧率模块（需要管理员权限；没有权限时会自动降级）
              --scan-games        扫描游戏库并自动关联目标进程对应的游戏（含画质配置解读）
              --game     <名字>   直接指定游戏名（会先扫描游戏库）
              --no-stop-on-exit   目标进程退出后不自动结束采样
              --help              显示本帮助

            示例：
              wiisl.exe --cli --process MyGame --seconds 60 --scan-games --out D:\reports
            """);
    }

    private static (int Pid, string Name)? FindByName(string name)
    {
        try
        {
            var matches = Process.GetProcessesByName(name);
            try
            {
                if (matches.Length == 0) return null;
                var p = matches[0];
                return (p.Id, p.ProcessName);
            }
            finally
            {
                foreach (var m in matches) m.Dispose();
            }
        }
        catch { return null; }
    }

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    private static string Fmt(double? v, string unit, int digits = 1)
        => v.HasValue ? v.Value.ToString("F" + digits, CultureInfo.InvariantCulture) + unit : "—";

    private static string TrySize(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return len >= 1024 * 1024 ? $"{len / 1024.0 / 1024.0:F2} MB" : $"{len / 1024.0:F1} KB";
        }
        catch { return "大小未知"; }
    }
}
