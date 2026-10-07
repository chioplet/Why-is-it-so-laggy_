using System.Text;
using System.Text.Json;
using Gpd.Core.Reporting;

namespace Gpd.Core.Analysis;

/// <summary>
/// 自测：用明确标注为合成数据的采样序列跑通「汇总计算 → 诊断分析 → Markdown/CSV/JSON 报告」整条链路，
/// 并把关键结果与真实落盘文件的绝对路径、字节数一起返回。
/// 任何一项校验不通过都会抛 <see cref="InvalidOperationException"/>，所以"能返回文本"就等于自测通过。
/// </summary>
public static class SelfTest
{
    /// <summary>合成数据标记，会写进每个采样点的备注里，避免与真实采集数据混淆。</summary>
    public const string SelfTestMarker = "self-test：合成数据，仅用于验证分析链路，不代表任何真实机器";

    /// <summary>合成数据里人为制造的卡顿帧位置（帧时间超过中位数 2 倍）。</summary>
    private static readonly int[] StutterSamples = [10, 20, 28];

    public static string ToolVersion =>
        typeof(SelfTest).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>用默认输出目录（%TEMP%\GpdSelfTest）跑自测。</summary>
    public static string RunSelfTest() => RunSelfTest(null, 30);

    /// <summary>跑自测并返回摘要文本。</summary>
    public static string RunSelfTest(string? outputDirectory, int sampleCount = 30)
    {
        if (sampleCount < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "自测样本数不能少于 10，否则帧率统计不会生效。");
        }

        var report = BuildSyntheticReport(sampleCount);
        var directory = ReportFileHelper.EnsureDirectory(
            string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetTempPath(), "GpdSelfTest")
                : outputDirectory);

        var files = ReportWriterFactory.WriteAllSupported(report, directory);
        var problems = new List<string>();

        var sb = new StringBuilder(8 * 1024);
        sb.Append("================ wiisl 自测 ================").Append('\n');
        sb.Append("数据来源：").Append(SelfTestMarker).Append('\n');
        sb.Append("合成采样点：").Append(sampleCount).Append(" 个；输出目录：").Append(directory).Append('\n').Append('\n');

        // ── 汇总指标关键字段 ────────────────────────────────────────────────
        var s = report.Summary;
        sb.Append("── 汇总指标（关键字段）──").Append('\n');
        sb.Append("瓶颈判定：").Append(s.BottleneckVerdict).Append('\n');
        sb.Append("CPU 平均/最高占用：").Append(ValueFormat.Percent(s.CpuAvgPercent)).Append(" / ").Append(ValueFormat.Percent(s.CpuMaxPercent)).Append('\n');
        sb.Append("GPU 平均/最高占用：").Append(ValueFormat.Percent(s.GpuAvgUtilPercent)).Append(" / ").Append(ValueFormat.Percent(s.GpuMaxUtilPercent)).Append('\n');
        sb.Append("GPU 温度 平均/最高：").Append(ValueFormat.Temp(s.GpuAvgTempC)).Append(" / ").Append(ValueFormat.Temp(s.GpuMaxTempC))
          .Append("；功耗触顶样本占比：").Append(ValueFormat.Percent(s.GpuPowerLimitedPercent)).Append('\n');
        sb.Append("系统最低可用内存：").Append(ValueFormat.Memory(s.SystemMinAvailableMemoryMb))
          .Append("；页面文件最高占用：").Append(ValueFormat.Percent(s.PageFileMaxPercent)).Append('\n');
        sb.Append("帧率 平均/最低：").Append(ValueFormat.Fps(s.FpsAvg)).Append(" / ").Append(ValueFormat.Fps(s.FpsMin))
          .Append("；1% Low：").Append(ValueFormat.Fps(s.Fps1PercentLow))
          .Append("；0.1% Low：").Append(ValueFormat.Fps(s.Fps01PercentLow)).Append('\n');
        sb.Append("卡顿帧：").Append(s.StutterCount).Append(" 次，")
          .Append(s.StuttersPerMinute.HasValue ? ValueFormat.Number(s.StuttersPerMinute, 2) + " 次/分钟" : ValueFormat.NoData)
          .Append("；帧时间 P99：").Append(ValueFormat.Ms(s.FrameTimeP99Ms)).Append('\n');
        sb.Append("CPU/GPU 占用差：").Append(ValueFormat.Percent(s.CpuGpuGapPercent)).Append('\n');
        sb.Append("采样时长/点数：").Append(ValueFormat.Duration(s.DurationSeconds)).Append(" / ").Append(s.SampleCount).Append('\n').Append('\n');

        // ── 诊断结论 ────────────────────────────────────────────────────────
        sb.Append("── 诊断结论（共 ").Append(report.Findings.Count).Append(" 条，按严重度排序）──").Append('\n');
        for (var i = 0; i < report.Findings.Count; i++)
        {
            var f = report.Findings[i];
            sb.Append(i + 1).Append(". [").Append(f.Severity).Append("] ").Append(f.Title)
              .Append("（").Append(f.Category).Append("）").Append('\n');
            sb.Append("   依据：").Append(f.Evidence.Count > 0 ? f.Evidence[0] : "（无）").Append('\n');
            sb.Append("   建议：").Append(f.Recommendations.Count > 0 ? f.Recommendations[0] : "（无）").Append('\n');
        }

        sb.Append('\n');

        // ── 报告文件与校验 ──────────────────────────────────────────────────
        sb.Append("── 落盘文件与校验 ──").Append('\n');
        foreach (var (format, path) in files)
        {
            var exists = File.Exists(path);
            var bytes = exists ? new FileInfo(path).Length : 0;
            sb.Append(format).Append(" → ").Append(path).Append("（").Append(bytes).Append(" 字节）").Append('\n');

            if (!exists || bytes == 0)
            {
                problems.Add($"{format} 报告没有写出内容：{path}");
            }
        }

        VerifyMarkdown(files, report, problems, sb);
        VerifyCsv(files, report, sampleCount, problems, sb);
        VerifyJson(files, report, sampleCount, problems, sb);

        sb.Append('\n');
        if (problems.Count == 0)
        {
            sb.Append("校验结果：全部通过（合成数据链路可用，报告内容与汇总指标一致）").Append('\n');
            sb.Append("=========================================================").Append('\n');
            return sb.ToString();
        }

        sb.Append("校验结果：未通过，共 ").Append(problems.Count).Append(" 项").Append('\n');
        foreach (var problem in problems)
        {
            sb.Append("  - ").Append(problem).Append('\n');
        }

        sb.Append("=========================================================").Append('\n');
        throw new InvalidOperationException("自测校验未通过：" + Environment.NewLine + sb);
    }

    /// <summary>构造一份明确标注为合成数据的诊断报告。</summary>
    public static DiagnosisReport BuildSyntheticReport(int sampleCount = 30)
    {
        const double interval = 1.0;
        var startedAt = DateTimeOffset.Now.AddSeconds(-sampleCount * interval);
        var session = BuildSyntheticSession(sampleCount, interval, startedAt);

        var report = new DiagnosisReport
        {
            Session = session,
            Game = session.Game,
            GeneratedAt = DateTimeOffset.Now,
            ToolVersion = ToolVersion,
        };

        report.Summary = SummaryCalculator.Calculate(session);
        report.GraphicsInsights = BuildGraphicsInsights();
        report.Cores = BuildCores(session);
        report.Findings = DiagnosisEngine.Analyze(report);
        return report;
    }

    private static SampleSession BuildSyntheticSession(int sampleCount, double interval, DateTimeOffset startedAt)
    {
        var samples = new List<PerformanceSample>(sampleCount);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i * interval;
            var slow = Array.IndexOf(StutterSamples, i) >= 0;
            var fps = slow ? 24.0 : 62.0 + 6.0 * Math.Sin(i * 0.55);
            var wobble = Math.Sin(i * 0.7);
            var coreCount = 16;

            samples.Add(new PerformanceSample
            {
                ElapsedSeconds = t,
                Timestamp = startedAt.AddSeconds(t),

                // CPU：整机占用不高（约 34%），游戏进程主要吃 5 个核心左右
                CpuTotalPercent = Clamp(34 + 4 * wobble, 0, 100),
                CpuFrequencyMhz = 4200 + 120 * Math.Cos(i * 0.4),
                CpuPerformancePercent = Clamp(33 + 3 * wobble, 0, 100),
                CpuUtilityPercent = Clamp(35 + 3 * wobble, 0, 100),
                CpuPackageTempC = 64 + 3 * wobble,
                CpuPackagePowerW = 70 + 6 * wobble,
                CpuLogicalCount = coreCount,
                CpuPerCorePercent = Enumerable.Range(0, coreCount)
                    .Select(c => Clamp(32 + 26 * Math.Sin(i * 0.3 + c * 1.1), 5, 78))
                    .ToList(),
                CpuCoreTempC = Enumerable.Range(0, 8)
                    .ToDictionary(c => c, c => 62.0 + 4 * Math.Sin(c * 0.9)),

                // GPU：接近满载 + 功耗长期贴上限，温度刻意压在 83°C 以下
                GpuName = "Self-Test GPU 8GB",
                GpuUtilPercent = Clamp(96 + 3 * Math.Abs(Math.Sin(i * 0.35)), 0, 100),
                GpuClockMhz = 2700 + 60 * Math.Sin(i * 0.5),
                GpuMemClockMhz = 10500,
                GpuTempC = 79 + 3 * Math.Sin(i * 0.45),
                GpuVoltageMv = 1050 + 25 * Math.Sin(i),
                GpuPowerW = 238 + 10 * Math.Abs(Math.Sin(i * 0.4)),
                GpuPowerLimitW = 250,
                GpuFanPercent = 68 + 6 * Math.Abs(Math.Sin(i * 0.2)),
                GpuProcessDedicatedMemoryMb = 7200 + 200 * Math.Sin(i * 0.3),
                GpuProcessSharedMemoryMb = 1200,
                GpuTotalDedicatedMemoryMb = 7500,
                GpuTotalDedicatedMemoryLimitMb = 8192,

                // 内存：可用内存压到 2048MB 以下，提交内存与页面文件同时偏高
                ProcessWorkingSetMb = 9300 + 300 * Math.Sin(i * 0.2),
                ProcessPrivateMemoryMb = 8900,
                ProcessCpuPercent = 500 + 40 * Math.Sin(i * 0.6),
                SystemAvailableMemoryMb = 1500 + 900 * (0.5 + 0.5 * Math.Sin(i * 0.2)),
                SystemCommittedPercent = Clamp(91 + 3 * Math.Sin(i * 0.25), 0, 100),
                PageFilePercent = Clamp(56 + 6 * Math.Sin(i * 0.3), 0, 100),
                DiskPercent = Clamp(8 + 5 * Math.Sin(i * 0.5), 0, 100),
                DiskQueueLength = 0.3 + 0.2 * Math.Abs(Math.Sin(i * 0.9)),

                // 帧率：基准约 62FPS，第 10/20/28 个采样点人为制造卡顿帧
                FpsSource = "self-test 合成序列",
                Fps = fps,
                FpsAverage = fps,
                FrameTimeMs = 1000.0 / fps,
                Fps1PercentLow = fps,
                Fps01PercentLow = fps,

                Notes = [SelfTestMarker],
            });
        }

        return new SampleSession
        {
            ProcessName = "SelfTestGame",
            ProcessId = 4242,
            ProcessPath = @"C:\SelfTest\SelfTestGame.exe",
            Game = new GameInfo
            {
                Name = "自测游戏（合成）",
                Platform = GamePlatform.Standalone,
                InstallDir = @"C:\SelfTest",
                ExecutablePath = @"C:\SelfTest\SelfTestGame.exe",
                ProcessName = "SelfTestGame",
                AppId = "self-test",
                LastPlayed = startedAt,
                PlayTime = TimeSpan.FromHours(12.5),
                DiscoverySource = SelfTestMarker,
                ConfigFiles =
                [
                    new ConfigFile
                    {
                        Path = @"C:\SelfTest\graphics.ini",
                        Format = "ini",
                        Purpose = "图形设置（合成）",
                        SizeBytes = 4096,
                        LastWriteTime = startedAt,
                        Entries =
                        [
                            new ConfigEntry { Section = "Graphics", Key = "RayTracing", Value = "1" },
                            new ConfigEntry { Section = "Graphics", Key = "VSync", Value = "0" },
                            new ConfigEntry { Section = "Graphics", Key = "ShadowQuality", Value = "2" },
                        ],
                    },
                ],
            },
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(sampleCount * interval),
            IntervalSeconds = interval,
            StopReason = "SelfTest",
            Samples = samples,
            Machine = new MachineInfo
            {
                OsName = "Windows 11 专业版（self-test 合成）",
                OsVersion = "10.0",
                OsBuild = "26100",
                CpuName = "Self-Test CPU 8 核 16 线程",
                CpuPhysicalCores = 8,
                CpuLogicalProcessors = 16,
                CpuNominalMhz = 3600,
                TotalMemoryMb = 16384,
                IsElevated = false,
                PowerPlanName = "平衡（self-test 合成）",
                GameModeEnabled = true,
                HardwareGpuSchedulingEnabled = false,
                NvidiaDriverVersion = "000.00（self-test 合成）",
                Gpus =
                [
                    new GpuInfo
                    {
                        Name = "Self-Test GPU 8GB",
                        VramMb = 8192,
                        DriverVersion = "000.00",
                        IsVirtualDisplayAdapter = false,
                    },
                ],
                Capabilities =
                [
                    new CapabilityProbe { Name = "GPU 引擎占用", Available = true, Detail = "self-test 合成数据，永远可用" },
                    new CapabilityProbe { Name = "GPU 每进程显存", Available = false, Detail = "self-test 故意置为不可用，用于验证报告里的能力说明" },
                    new CapabilityProbe { Name = "CPU 封装温度", Available = true, Detail = "self-test 合成数据，永远可用" },
                ],
            },
            Warnings = ["self-test：本报告全部数字来自合成序列，不可用于判断这台机器的真实性能"],
        };
    }

    private static List<GraphicsSettingInsight> BuildGraphicsInsights() =>
    [
        new GraphicsSettingInsight
        {
            SettingName = "光线追踪",
            SourceKey = "Graphics.RayTracing",
            RawValue = "1",
            NormalizedValue = "开",
            PerformanceImpact = 5,
            Comment = "合成自测项：光追开着且显卡已经满载，是最值得先关的一项。",
        },
        new GraphicsSettingInsight
        {
            SettingName = "垂直同步",
            SourceKey = "Graphics.VSync",
            RawValue = "0",
            NormalizedValue = "关",
            PerformanceImpact = 1,
            Comment = "合成自测项：已经关闭，不会限制帧率。",
        },
        new GraphicsSettingInsight
        {
            SettingName = "阴影质量",
            SourceKey = "Graphics.ShadowQuality",
            RawValue = "2",
            NormalizedValue = "中",
            PerformanceImpact = 2,
            Comment = "合成自测项：中等档位，影响有限。",
        },
    ];

    private static List<CpuCoreSnapshot> BuildCores(SampleSession session)
    {
        var last = session.Samples[^1];
        var cores = new List<CpuCoreSnapshot>();
        var perCore = last.CpuPerCorePercent ?? [];

        for (var i = 0; i < perCore.Count; i++)
        {
            double? tempC = null;
            if (last.CpuCoreTempC is not null && last.CpuCoreTempC.TryGetValue(i % 8, out var temp))
            {
                tempC = temp;
            }

            cores.Add(new CpuCoreSnapshot
            {
                Index = i,
                Label = i < 8 ? $"物理核心 {i}（主线程）" : $"物理核心 {i - 8}（超线程）",
                UsagePercent = perCore[i],
                TempC = tempC,
            });
        }

        return cores;
    }

    // ── 校验 ────────────────────────────────────────────────────────────────

    private static void VerifyMarkdown(Dictionary<ReportFormat, string> files, DiagnosisReport report, List<string> problems, StringBuilder sb)
    {
        if (!files.TryGetValue(ReportFormat.Markdown, out var path) || !File.Exists(path))
        {
            problems.Add("Markdown 报告缺失");
            return;
        }

        var text = File.ReadAllText(path);
        var checks = new (string Name, bool Ok)[]
        {
            ("含一至九全部章节", text.Contains("## 一、结论速览", StringComparison.Ordinal)
                && text.Contains("## 二、硬件与系统", StringComparison.Ordinal)
                && text.Contains("## 三、采样概况", StringComparison.Ordinal)
                && text.Contains("## 四、汇总指标", StringComparison.Ordinal)
                && text.Contains("## 五、时间序列", StringComparison.Ordinal)
                && text.Contains("## 六、诊断结论", StringComparison.Ordinal)
                && text.Contains("## 七、画质解读", StringComparison.Ordinal)
                && text.Contains("## 八、逐核快照", StringComparison.Ordinal)
                && text.Contains("## 九、数据来源与可信度", StringComparison.Ordinal)),
            ("含瓶颈判定", text.Contains(report.Summary.BottleneckVerdict, StringComparison.Ordinal)),
            ("含第 1 条结论标题", report.Findings.Count > 0 && text.Contains(report.Findings[0].Title, StringComparison.Ordinal)),
            ("含合成数据说明", text.Contains("self-test", StringComparison.Ordinal)),
        };

        foreach (var (name, ok) in checks)
        {
            sb.Append("  · Markdown 校验：").Append(name).Append(" → ").Append(ok ? "通过" : "失败").Append('\n');
            if (!ok)
            {
                problems.Add("Markdown 校验失败：" + name);
            }
        }
    }

    private static void VerifyCsv(Dictionary<ReportFormat, string> files, DiagnosisReport report, int sampleCount, List<string> problems, StringBuilder sb)
    {
        if (!files.TryGetValue(ReportFormat.Csv, out var samplesPath) || !File.Exists(samplesPath))
        {
            problems.Add("CSV 报告缺失");
            return;
        }

        var sampleLines = File.ReadAllLines(samplesPath);
        var expected = sampleCount + 2; // 表头 + 中文说明 + 数据行
        var sampleOk = sampleLines.Length == expected;
        sb.Append("  · CSV samples 行数：").Append(sampleLines.Length).Append("（期望 ").Append(expected).Append("）→ ")
          .Append(sampleOk ? "通过" : "失败").Append('\n');
        if (!sampleOk)
        {
            problems.Add($"CSV samples 行数不符：实际 {sampleLines.Length}，期望 {expected}");
        }

        if (sampleLines.Length >= 2 && !sampleLines[0].StartsWith("elapsed_seconds,", StringComparison.Ordinal))
        {
            problems.Add("CSV samples 第一行不是英文表头");
        }

        if (sampleLines.Length >= 2 && !sampleLines[1].StartsWith("# 列说明：", StringComparison.Ordinal))
        {
            problems.Add("CSV samples 第二行不是中文列说明");
        }

        var findingsPath = Path.Combine(Path.GetDirectoryName(samplesPath)!, CsvReportWriter.FindingsFileName(report));
        if (!File.Exists(findingsPath))
        {
            problems.Add("CSV findings 文件缺失：" + findingsPath);
            return;
        }

        var findingLines = File.ReadAllLines(findingsPath);
        var findingOk = findingLines.Length == report.Findings.Count + 2;
        sb.Append("  · CSV findings 行数：").Append(findingLines.Length).Append("（期望 ").Append(report.Findings.Count + 2).Append("）→ ")
          .Append(findingOk ? "通过" : "失败").Append('\n');
        if (!findingOk)
        {
            problems.Add($"CSV findings 行数不符：实际 {findingLines.Length}，期望 {report.Findings.Count + 2}");
        }
    }

    private static void VerifyJson(Dictionary<ReportFormat, string> files, DiagnosisReport report, int sampleCount, List<string> problems, StringBuilder sb)
    {
        if (!files.TryGetValue(ReportFormat.Json, out var path) || !File.Exists(path))
        {
            problems.Add("JSON 报告缺失");
            return;
        }

        var text = File.ReadAllText(path);
        var checks = new List<(string Name, bool Ok)>();

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var meta = root.GetProperty("meta");
            checks.Add(("JSON 可解析", true));
            checks.Add(($"meta.sampleCount = {sampleCount}", meta.GetProperty("sampleCount").GetInt32() == sampleCount));
            checks.Add(("samples 数组长度正确", root.GetProperty("samples").GetArrayLength() == sampleCount));
            checks.Add(("findings 数组长度正确", root.GetProperty("findings").GetArrayLength() == report.Findings.Count));
            checks.Add(("summary 存在", root.TryGetProperty("summary", out _)));
            checks.Add(("machine 存在", root.TryGetProperty("machine", out _)));
        }
        catch (JsonException ex)
        {
            checks.Add(("JSON 可解析（" + ex.Message + "）", false));
        }

        // 中文没有被 \uXXXX 转义
        checks.Add(("中文未被转义", text.Contains("瓶颈", StringComparison.Ordinal) && !text.Contains("\\u74f6", StringComparison.Ordinal)));

        foreach (var (name, ok) in checks)
        {
            sb.Append("  · JSON 校验：").Append(name).Append(" → ").Append(ok ? "通过" : "失败").Append('\n');
            if (!ok)
            {
                problems.Add("JSON 校验失败：" + name);
            }
        }
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : (value > max ? max : value);
}
