using System.Globalization;
using System.IO;
using System.Text;
using Gpd.Core;
using Gpd.Core.Analysis;
using Gpd.Core.Reporting;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Gpd.App.Reporting;

/// <summary>
/// PDF 报告输出（QuestPDF）。放在 Gpd.App 而不是 Gpd.Core，是为了让核心分析库保持零 UI/渲染依赖：
/// Gpd.Core 只输出 Markdown/CSV/JSON，PDF 由应用层负责。
/// 内容与 Markdown 报告对齐：封面 → 结论速览 → 硬件与系统 → 采样概况 → 汇总指标 → 诊断结论与建议 → 画质解读 → 数据来源与可信度。
/// </summary>
public sealed class PdfReportWriter : IReportWriter
{
    /// <summary>正文（含标题、段落、结论文字）字号。</summary>
    public const float BodyFontSize = 10f;

    /// <summary>表格字号。表格列多、中文长，比正文小一档才放得下。</summary>
    public const float TableFontSize = 8.5f;

    /// <summary>表格里"长文本"列（实测依据、建议）用的字号，与表格主体一致，单独命名便于统一调整。</summary>
    public const float TableTextFontSize = 8.5f;

    private static readonly object FontGate = new();

    /// <summary>
    /// 中文字体候选。按优先级排列：微软雅黑 → 黑体 → 宋体 → 等线。
    /// QuestPDF 2026.9.0 起 <c>Settings.UseSystemFonts</c> 默认为 <c>false</c>、缺字形默认直接抛异常，
    /// 所以中文报告必须显式注册字体文件（下面 <see cref="EnsureFonts"/>），否则要么抛
    /// <c>DocumentDrawingException</c>，要么整篇中文变成方块。
    /// </summary>
    private static readonly string[] FontFamilies =
    [
        "Microsoft YaHei",
        "微软雅黑",
        "SimHei",
        "黑体",
        "SimSun",
        "宋体",
        "DengXian",
        "Segoe UI",
        "Arial",
    ];

    private static readonly string[] FontFileCandidates =
    [
        "msyh.ttc",
        "simhei.ttf",
        "simsun.ttc",
        "Deng.ttf",
    ];

    private static string? _fontDiagnostics;

    /// <summary>
    /// 初始化 QuestPDF 许可与中文字体，返回本次真实生效的字体诊断信息（供自测/日志引用）。
    /// 字体注册是"尽力而为"：单个字体文件损坏不应让整份报告失败，但失败原因会被收集进返回值，不会静默丢弃。
    /// </summary>
    public static string EnsureFonts()
    {
        lock (FontGate)
        {
            if (_fontDiagnostics is not null)
            {
                return _fontDiagnostics;
            }

            // 社区版许可：必须在生成任何文档之前设置，否则 QuestPDF 会抛异常。
            QuestPDF.Settings.License = LicenseType.Community;

            // 本机装了中文字体就用系统字体兜底；字形缺失仍然抛异常（宁可报错也不要画出方块）。
            QuestPDF.Settings.UseSystemFonts = true;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;

            var fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrWhiteSpace(fontDirectory))
            {
                fontDirectory = @"C:\Windows\Fonts";
            }

            var builder = new StringBuilder();
            builder.Append("QuestPDF ").Append(GetQuestPdfVersion());
            builder.Append("；License=Community；UseSystemFonts=true；字体目录 ").Append(fontDirectory);

            var registered = new List<string>();
            var skipped = new List<string>();
            foreach (var fileName in FontFileCandidates)
            {
                var path = Path.Combine(fontDirectory, fileName);
                if (!File.Exists(path))
                {
                    skipped.Add(fileName + "（文件不存在）");
                    continue;
                }

                try
                {
                    FontManager.RegisterFontFromFile(path);
                    registered.Add(fileName);
                }
                catch (Exception ex)
                {
                    // 不吞异常：原因写进诊断串，报告和自测都能看到到底哪个字体没注册上。
                    skipped.Add($"{fileName}（{ex.GetType().Name}：{ex.Message}）");
                }
            }

            builder.Append("；已注册字体：").Append(registered.Count == 0 ? "无" : string.Join("、", registered));
            if (skipped.Count > 0)
            {
                builder.Append("；跳过：").Append(string.Join("、", skipped));
            }

            _fontDiagnostics = builder.ToString();
            return _fontDiagnostics;
        }
    }

    private static string GetQuestPdfVersion() =>
        typeof(Document).Assembly.GetName().Version?.ToString() ?? "未知";

    /// <inheritdoc />
    public string Write(DiagnosisReport report, string outputDirectory, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (format != ReportFormat.Pdf)
        {
            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "PdfReportWriter 只负责 PDF；Markdown/CSV/JSON 请用 Gpd.Core.Reporting.ReportWriterFactory。");
        }

        var directory = ReportFileHelper.EnsureDirectory(outputDirectory);
        var path = Path.Combine(directory, ReportFileHelper.BuildFileName(report, string.Empty, "pdf"));

        EnsureFonts();

        // 异常一律向上抛：写不出来就让调用方看见，不做"生成成功"的假象。
        BuildDocument(report, directory).GeneratePdf(path);
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// 只构建文档对象、不落盘，便于自测与后续换 <c>GenerateImages</c> 做像素校验。
    /// <paramref name="outputDirectory"/> 用于列出同批报告文件，缺省时退回临时报告目录。
    /// </summary>
    public static IDocument BuildDocument(DiagnosisReport report, string? outputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        EnsureFonts();
        var directory = ReportFileHelper.EnsureDirectory(outputDirectory);
        return Document.Create(container =>
        {
            ComposeCover(container, report);
            ComposeBody(container, report, directory);
        });
    }

    // ─────────────────────────── 封面 ───────────────────────────

    private static void ComposeCover(IDocumentContainer container, DiagnosisReport report)
    {
        var session = report.Session;

        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(48);
            page.DefaultTextStyle(style => style
                .FontSize(BodyFontSize)
                .FontFamily(FontFamilies)
                .FontColor(Colors.Grey.Darken3)
                .LineHeight(1.35f));

            page.Content().PaddingTop(150).Column(column =>
            {
                column.Spacing(10);

                column.Item().Text("游戏性能诊断报告").FontSize(30).Bold().FontColor(Colors.Blue.Darken3);

                column.Item().PaddingTop(4).Text($"目标进程：{Display(session.ProcessName)}")
                    .FontSize(13).FontColor(Colors.Grey.Darken2);

                column.Item().PaddingTop(16).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

                column.Item().PaddingTop(12).Text(BuildCoverMeta(report)).FontSize(BodyFontSize).LineHeight(1.6f);

                if (IsSelfTest(report))
                {
                    column.Item().PaddingTop(20).Background(Colors.Yellow.Lighten4).Padding(10)
                        .Text("注意：本报告由 self-test 合成数据生成，仅用于验证分析链路，不代表任何真实机器。")
                        .FontSize(BodyFontSize).Bold().FontColor(Colors.Orange.Darken3);
                }

                column.Item().PaddingTop(24).Text("报告由 wiisl 自动生成，结论均基于本次采样的实测数字。")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });
        });
    }

    private static string BuildCoverMeta(DiagnosisReport report)
    {
        var session = report.Session;
        var builder = new StringBuilder();
        builder.Append("生成时间：").Append(FormatTime(report.GeneratedAt)).Append('\n');
        builder.Append("工具版本：").Append(Display(report.ToolVersion)).Append('\n');
        builder.Append("采样时间：").Append(FormatTime(session.StartedAt) is var started && started.Length > 0
            ? started + " → " + FormatTime(session.EndedAt)
            : "未记录").Append('\n');
        builder.Append("采样点数：").Append(session.Samples.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" 个（其中带帧率 ").Append(session.FpsSampleCount.ToString(CultureInfo.InvariantCulture)).Append(" 个）").Append('\n');
        builder.Append("处理器：").Append(Display(session.Machine.CpuName)).Append('\n');
        builder.Append("操作系统：").Append(Display(session.Machine.OsName)).Append(' ')
            .Append(Display(session.Machine.OsVersion)).Append("（Build ").Append(Display(session.Machine.OsBuild)).Append('）').Append('\n');
        builder.Append("瓶颈判定：").Append(Display(report.Summary.BottleneckVerdict));
        return builder.ToString();
    }

    // ─────────────────────────── 正文 ───────────────────────────

    private static void ComposeBody(IDocumentContainer container, DiagnosisReport report, string outputDirectory)
    {
        var session = report.Session;

        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(style => style
                .FontSize(BodyFontSize)
                .FontFamily(FontFamilies)
                .FontColor(Colors.Grey.Darken3)
                .LineHeight(1.3f));

            page.Header().BorderBottom(1).BorderColor(Colors.Grey.Lighten1).PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Text("游戏性能诊断报告").FontSize(9).FontColor(Colors.Grey.Darken1);
                row.ConstantItem(220).AlignRight()
                    .Text($"{Display(session.ProcessName)} · {FormatTime(report.GeneratedAt)}")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });

            page.Content().PaddingVertical(6).Column(column =>
            {
                ComposeVerdictSection(column, report);
                ComposeMachineSection(column, report);
                ComposeSessionSection(column, report);
                ComposeSummarySection(column, report);
                ComposeFindingsSection(column, report);
                ComposeGraphicsSection(column, report);
                ComposeSourceSection(column, report, outputDirectory);
            });

            page.Footer().AlignRight().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Darken1));
                text.Span("第 ");
                text.CurrentPageNumber();
                text.Span(" 页 / 共 ");
                text.TotalPages();
                text.Span(" 页");
            });
        });
    }

    private static void ComposeVerdictSection(ColumnDescriptor column, DiagnosisReport report)
    {
        SectionTitle(column, "一、结论速览");

        var summary = report.Summary;
        var ordered = OrderedFindings(report);

        column.Item().Border(1).BorderColor(Colors.Blue.Lighten2).Background(Colors.Blue.Lighten5)
            .Padding(10).Column(box =>
            {
                box.Item().Text($"瓶颈判定：{Display(summary.BottleneckVerdict)}")
                    .FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                box.Item().PaddingTop(4).Text($"共 {ordered.Count} 条结论：" + BuildSeverityCounts(ordered))
                    .FontSize(BodyFontSize).FontColor(Colors.Grey.Darken2);
            });

        if (ordered.Count > 0)
        {
            column.Item().PaddingTop(8).Text("按严重程度排序，第一条最需要先处理：")
                .FontSize(BodyFontSize).FontColor(Colors.Grey.Darken1);

            DataTable(
                column,
                ["#", "严重度", "类别", "结论"],
                [45, 60, 90, 400],
                ordered.Select((finding, index) => new[]
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    SeverityText(finding.Severity),
                    Display(finding.Category),
                    Display(finding.Title),
                }).ToList(),
                severityColumn: 1);

            // 只有真正"需要处理"的结论（警告以上）才配当"最该先做的一件事"；否则全是提示/正常时
            // 会把"装个 HWiNFO 才能读到 CPU 封装温度"这类采集能力说明推到第一位。与 MarkdownReportWriter 保持同一规则。
            var top = ordered.FirstOrDefault(f =>
                f.Recommendations is { Count: > 0 } &&
                f.Severity is FindingSeverity.Critical or FindingSeverity.Warning);

            column.Item().PaddingTop(8).Border(1).BorderColor(Colors.Orange.Lighten2)
                .Background(Colors.Orange.Lighten5).Padding(10).Column(box =>
                {
                    if (top is not null)
                    {
                        box.Item().Text($"最该先做的一件事：{Display(top.Title)}")
                            .FontSize(11).Bold().FontColor(Colors.Orange.Darken3);
                        box.Item().PaddingTop(4).Text(top.Recommendations[0]).FontSize(BodyFontSize);
                    }
                    else
                    {
                        box.Item().Text("本次采样没有发现需要立即处理的问题")
                            .FontSize(11).Bold().FontColor(Colors.Green.Darken3);
                        box.Item().PaddingTop(4)
                            .Text("想拿到更完整的结论，可以按「数据来源与可信度」一节补齐受限项后重测。")
                            .FontSize(BodyFontSize);
                    }
                });
        }
    }

    private static void ComposeMachineSection(ColumnDescriptor column, DiagnosisReport report)
    {
        var machine = report.Session.Machine;
        SectionTitle(column, "二、硬件与系统");

        // 注意：这里必须用集合表达式（List<string[]> rows = [...]）而不是对象初始化器，
        // 否则 `["a", "b"]` 会被解析成索引器初始化 `[key] = value`，直接语法错误。
        List<string[]> rows =
        [
            ["操作系统", Display(machine.OsName), $"{Display(machine.OsVersion)}（Build {Display(machine.OsBuild)}）"],
            ["处理器", Display(machine.CpuName), $"{Text(machine.CpuPhysicalCores)} 物理核 / {Text(machine.CpuLogicalProcessors)} 逻辑核，标称 {ValueFormat.Mhz(machine.CpuNominalMhz)}"],
            ["内存", ValueFormat.Memory(machine.TotalMemoryMb), "整机物理内存总量"],
            ["电源计划", Display(machine.PowerPlanName), "高性能/卓越性能更利于稳定帧率"],
            ["游戏模式", BoolText(machine.GameModeEnabled), "Windows 游戏模式是否开启"],
            ["硬件加速 GPU 计划", BoolText(machine.HardwareGpuSchedulingEnabled), "HAGS 是否开启"],
            ["NVIDIA 驱动", Display(machine.NvidiaDriverVersion), "显卡驱动版本"],
            ["管理员权限", machine.IsElevated ? "是" : "否", machine.IsElevated ? "可读取全部性能计数器" : "部分计数器与逐帧采集会受限"],
        ];

        KeyValueTable(column, rows);

        if (machine.Gpus.Count > 0)
        {
            column.Item().PaddingTop(8).Text("显卡").FontSize(11).Bold();
            DataTable(
                column,
                ["显卡", "显存", "驱动版本", "虚拟显示适配器"],
                [220, 90, 110, 90],
                machine.Gpus.Select(gpu => new[]
                {
                    Display(gpu.Name),
                    gpu.VramMb.HasValue ? ValueFormat.Memory(gpu.VramMb) : ValueFormat.NoData,
                    Display(gpu.DriverVersion),
                    gpu.IsVirtualDisplayAdapter ? "是" : "否",
                }).ToList());
        }

        if (report.Cores.Count > 0)
        {
            column.Item().PaddingTop(8).Text("CPU 逐核快照").FontSize(11).Bold();
            DataTable(
                column,
                ["核心", "占用", "温度"],
                [200, 120, 120],
                report.Cores.Select(core => new[]
                {
                    $"{Display(core.Label)}（#{core.Index.ToString(CultureInfo.InvariantCulture)}）",
                    ValueFormat.Percent(core.UsagePercent),
                    ValueFormat.Temp(core.TempC),
                }).ToList());
        }
    }

    private static void ComposeSessionSection(ColumnDescriptor column, DiagnosisReport report)
    {
        var session = report.Session;
        SectionTitle(column, "三、采样概况");

        List<string[]> rows =
        [
            ["目标进程", Display(session.ProcessName), $"PID {session.ProcessId.ToString(CultureInfo.InvariantCulture)}"],
            ["进程路径", Display(session.ProcessPath), "被采集的游戏/程序所在位置"],
            ["采样时长", ValueFormat.Duration(report.Summary.DurationSeconds), "时长过短（不足 5 秒）时结论不可靠"],
            ["采样点数", $"{session.Samples.Count.ToString(CultureInfo.InvariantCulture)} 个", $"带帧率的样本 {session.FpsSampleCount.ToString(CultureInfo.InvariantCulture)} 个（判定 1% Low 至少需要 {SummaryCalculator.MinimumFpsSamples.ToString(CultureInfo.InvariantCulture)} 个）"],
            ["采样间隔", $"{ValueFormat.Number(session.IntervalSeconds, 2)} 秒", "两次采样之间的间隔"],
            ["开始时间", FormatTime(session.StartedAt), "本次采样起点"],
            ["结束时间", FormatTime(session.EndedAt), "本次采样终点"],
            ["停止原因", Gpd.Core.Reporting.MarkdownReportWriter.DescribeStopReason(session.StopReason), "采样是怎么结束的"],
        ];

        var game = report.Game ?? session.Game;
        if (game is not null)
        {
            rows.Add(["识别到的游戏", Display(game.Name), $"来源：{Display(game.DiscoverySource)}"]);
            rows.Add(["游戏平台", Display(game.PlatformName), "来自游戏库识别"]);
            rows.Add(["安装目录", Display(game.InstallDir), "游戏本体所在目录"]);
            rows.Add(["可执行文件", Display(game.ExecutablePath), "实际被采集的进程"]);
            rows.Add(["应用 ID", Display(game.AppId), "商店/平台内的唯一标识"]);
            rows.Add(["最近一次游玩", game.LastPlayed.HasValue ? FormatTime(game.LastPlayed.Value) : ValueFormat.NoData, "平台记录的上次启动时间"]);
            rows.Add(["累计游玩时长", game.PlayTime.HasValue ? ValueFormat.Number(game.PlayTime.Value.TotalHours, 1) + " 小时" : ValueFormat.NoData, "平台记录的累计时长"]);
            rows.Add(["画质配置文件", game.ConfigFiles.Count.ToString(CultureInfo.InvariantCulture) + " 个", "用于画质解读的配置来源"]);
        }

        KeyValueTable(column, rows);
    }

    private static void ComposeSummarySection(ColumnDescriptor column, DiagnosisReport report)
    {
        var summary = report.Summary;
        SectionTitle(column, "四、汇总指标");

        List<string[]> rows =
        [
            ["CPU 占用", $"平均 {ValueFormat.Percent(summary.CpuAvgPercent)}", $"最高 {ValueFormat.Percent(summary.CpuMaxPercent)}"],
            ["CPU 频率", $"平均 {ValueFormat.Mhz(summary.CpuAvgFrequencyMhz)}", $"最高 {ValueFormat.Mhz(summary.CpuMaxFrequencyMhz)}"],
            [summary.CpuTempTracksLoad == true ? "CPU 温度" : "热区温度（非 CPU 封装）",
             $"平均 {ValueFormat.Temp(summary.CpuAvgTempC)}", $"最高 {ValueFormat.Temp(summary.CpuMaxTempC)}"],
            ["CPU 功耗", $"平均 {ValueFormat.Watt(summary.CpuAvgPowerW)}", $"最高 {ValueFormat.Watt(summary.CpuMaxPowerW)}"],
            ["GPU 占用", $"平均 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}", $"最高 {ValueFormat.Percent(summary.GpuMaxUtilPercent)}"],
            ["GPU 频率", $"平均 {ValueFormat.Mhz(summary.GpuAvgClockMhz)}", $"最高 {ValueFormat.Mhz(summary.GpuMaxClockMhz)}"],
            ["GPU 温度", $"平均 {ValueFormat.Temp(summary.GpuAvgTempC)}", $"最高 {ValueFormat.Temp(summary.GpuMaxTempC)}"],
            ["GPU 功耗", $"平均 {ValueFormat.Watt(summary.GpuAvgPowerW)}", $"最高 {ValueFormat.Watt(summary.GpuMaxPowerW)}"],
            ["GPU 功耗上限", ValueFormat.Watt(summary.GpuPowerLimitW), "厂商设定的整卡功耗上限"],
            ["功耗墙触发占比", ValueFormat.Percent(summary.GpuPowerLimitedPercent), "实际功耗达到上限 95% 以上的样本占比"],
            ["GPU 高温占比", ValueFormat.Percent(summary.GpuThermalRiskPercent), "温度达到 83℃ 以上的样本占比"],
            ["进程工作集", $"平均 {ValueFormat.Memory(summary.ProcessAvgWorkingSetMb)}", $"最高 {ValueFormat.Memory(summary.ProcessMaxWorkingSetMb)}"],
            ["系统可用内存", ValueFormat.Memory(summary.SystemMinAvailableMemoryMb), "整段采样里的最低可用内存，比平均值更能说明问题"],
            ["页面文件占用", ValueFormat.Percent(summary.PageFileMaxPercent), "长期高位说明物理内存已经不够"],
            ["磁盘活动时间", $"平均 {ValueFormat.Percent(summary.DiskAvgPercent)}", $"最高 {ValueFormat.Percent(summary.DiskMaxPercent)}"],
            ["磁盘队列长度", ValueFormat.Number(summary.DiskMaxQueueLength, 2), "持续大于 2 说明磁盘是瓶颈"],
            ["帧率 FPS", $"平均 {ValueFormat.Fps(summary.FpsAvg)}", $"最低 {ValueFormat.Fps(summary.FpsMin)}／最高 {ValueFormat.Fps(summary.FpsMax)}"],
            ["1% Low", ValueFormat.Fps(summary.Fps1PercentLow), "最能反映主观卡顿的指标"],
            ["0.1% Low", ValueFormat.Fps(summary.Fps01PercentLow), "极端卡顿帧"],
            ["帧时间 P99", ValueFormat.Ms(summary.FrameTimeP99Ms), "99% 的帧不快于这个耗时"],
            ["帧时间标准差", ValueFormat.Ms(summary.FrameTimeStdDevMs), "数值越大帧生成越不稳"],
            ["卡顿帧", $"{summary.StutterCount.ToString(CultureInfo.InvariantCulture)} 次", $"约 {ValueFormat.Number(summary.StuttersPerMinute, 2)} 次/分钟（帧时间超过中位数 2 倍）"],
            ["CPU/GPU 占用差", ValueFormat.Percent(summary.CpuGpuGapPercent), "差值越大越可能一边倒"],
            ["瓶颈判定", Display(summary.BottleneckVerdict), "综合占用与帧率得出的结论"],
        ];

        KeyValueTable(column, rows);
    }

    private static void ComposeFindingsSection(ColumnDescriptor column, DiagnosisReport report)
    {
        var ordered = OrderedFindings(report);
        SectionTitle(column, "五、诊断结论与建议");

        if (ordered.Count == 0)
        {
            column.Item().Text("本次没有得出任何结论（采样数据不足或采集能力受限）。").FontSize(BodyFontSize);
            return;
        }

        foreach (var finding in ordered)
        {
            column.Item().PaddingTop(8).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(box =>
            {
                box.Item().Text($"[{SeverityText(finding.Severity)}] {Display(finding.Title)}")
                    .FontSize(11).Bold().FontColor(SeverityColor(finding.Severity));
                box.Item().PaddingTop(2).Text($"类别：{Display(finding.Category)}")
                    .FontSize(TableFontSize).FontColor(Colors.Grey.Darken1);

                AppendBulletBlock(box, "实测依据", finding.Evidence);
                AppendParagraph(box, "为什么会这样", finding.Explanation);
                AppendBulletBlock(box, "建议", finding.Recommendations);

                if (!string.IsNullOrWhiteSpace(finding.ExpectedGain))
                {
                    AppendParagraph(box, "预期效果", finding.ExpectedGain!);
                }

                if (finding.ConfigEvidence.Count > 0)
                {
                    AppendBulletBlock(box, "配置证据", finding.ConfigEvidence);
                }
            });
        }
    }

    private static void ComposeGraphicsSection(ColumnDescriptor column, DiagnosisReport report)
    {
        SectionTitle(column, "六、画质解读");
        var insights = report.GraphicsInsights;

        if (insights.Count == 0)
        {
            column.Item().Text("没有读到画质配置文件，因此无法判断画质设置与硬件的匹配情况。").FontSize(BodyFontSize);
            return;
        }

        DataTable(
            column,
            ["画质项", "来源键", "当前值", "规范值", "影响", "解读"],
            [90, 110, 55, 55, 35, 245],
            insights.Select(insight => new[]
            {
                Display(insight.SettingName),
                Display(insight.SourceKey),
                Display(insight.RawValue),
                Display(insight.NormalizedValue),
                insight.PerformanceImpact.ToString(CultureInfo.InvariantCulture),
                Display(insight.Comment),
            }).ToList());
    }

    private static void ComposeSourceSection(ColumnDescriptor column, DiagnosisReport report, string outputDirectory)
    {
        var session = report.Session;
        var machine = session.Machine;
        SectionTitle(column, "七、数据来源与可信度");

        column.Item().Text(BuildConfidenceParagraph(report)).FontSize(BodyFontSize);

        if (machine.Capabilities.Count > 0)
        {
            column.Item().PaddingTop(8).Text("采集能力").FontSize(11).Bold();
            DataTable(
                column,
                ["能力", "是否可用", "说明"],
                [150, 60, 350],
                machine.Capabilities.Select(capability => new[]
                {
                    Display(capability.Name),
                    capability.Available ? "可用" : "不可用",
                    Display(capability.Detail),
                }).ToList(),
                severityColumn: 1);
        }

        if (session.Warnings.Count > 0)
        {
            column.Item().PaddingTop(8).Text("采集过程的告警").FontSize(11).Bold();
            foreach (var warning in session.Warnings)
            {
                column.Item().PaddingTop(2).Text("· " + warning).FontSize(TableFontSize).FontColor(Colors.Orange.Darken3);
            }
        }

        var companions = ReportFileHelper.ListCompanionFiles(outputDirectory, report);

        column.Item().PaddingTop(8).Text("本次输出目录中的同名报告文件").FontSize(11).Bold();
        column.Item().PaddingTop(2).Text("（同一次分析写出的 Markdown/CSV/JSON 会列在这里；PDF 自身也可能在清单内）")
            .FontSize(8).FontColor(Colors.Grey.Darken1);
        if (companions.Count == 0)
        {
            column.Item().PaddingTop(2).Text("同目录下暂未发现其它格式的同名报告。").FontSize(TableFontSize);
        }
        else
        {
            foreach (var file in companions)
            {
                var size = TryGetFileSize(file);
                column.Item().PaddingTop(2).Text($"· {file}（{size}）").FontSize(TableFontSize);
            }
        }

        column.Item().PaddingTop(10).Text($"字体与渲染：{EnsureFonts()}").FontSize(8).FontColor(Colors.Grey.Darken1);
        column.Item().PaddingTop(4).Text($"工具版本 {Display(report.ToolVersion)} · 生成时间 {FormatTime(report.GeneratedAt)}")
            .FontSize(8).FontColor(Colors.Grey.Darken1);
    }

    private static string BuildConfidenceParagraph(DiagnosisReport report)
    {
        var summary = report.Summary;
        var builder = new StringBuilder();
        builder.Append("本报告全部结论都来自本次采样的实测数字，没有使用估算值或行业平均值。");

        if (summary.SampleCount < 5)
        {
            builder.Append("本次只采到 ").Append(summary.SampleCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 个采样点，样本严重不足，结论不可靠。");
        }
        else if (summary.DurationSeconds < 60)
        {
            builder.Append("本次采样时长 ").Append(ValueFormat.Duration(summary.DurationSeconds))
                .Append("，建议采满 60 秒以上再对比。");
        }
        else
        {
            builder.Append("采样点 ").Append(summary.SampleCount.ToString(CultureInfo.InvariantCulture))
                .Append(" 个，时长 ").Append(ValueFormat.Duration(summary.DurationSeconds)).Append("。");
        }

        if (report.Session.FpsSampleCount < SummaryCalculator.MinimumFpsSamples)
        {
            builder.Append("帧率样本不足 ").Append(SummaryCalculator.MinimumFpsSamples.ToString(CultureInfo.InvariantCulture))
                .Append(" 个，所以 1% Low、卡顿等帧相关指标留空而不是用 0 填充。");
        }

        var unavailable = report.Session.Machine.Capabilities.Count(c => !c.Available);
        if (unavailable > 0)
        {
            builder.Append("有 ").Append(unavailable.ToString(CultureInfo.InvariantCulture))
                .Append(" 项采集能力不可用，相关指标在报告里显示为「").Append(ValueFormat.NoData).Append("」。");
        }

        if (IsSelfTest(report))
        {
            builder.Append("注意：这是 self-test 合成数据生成的报告，仅用于验证分析链路。");
        }

        return builder.ToString();
    }

    // ─────────────────────────── 渲染基础设施 ───────────────────────────

    private static void SectionTitle(ColumnDescriptor column, string title)
    {
        column.Item().PaddingTop(12).PaddingBottom(5).Text(title)
            .FontSize(13).Bold().FontColor(Colors.Blue.Darken3);
    }

    private static void KeyValueTable(ColumnDescriptor column, IReadOnlyList<string[]> rows)
    {
        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(110);
                columns.ConstantColumn(150);
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("项目").FontSize(TableFontSize).Bold();
                header.Cell().Element(HeaderCell).Text("数值").FontSize(TableFontSize).Bold();
                header.Cell().Element(HeaderCell).Text("说明").FontSize(TableFontSize).Bold();
            });

            foreach (var row in rows)
            {
                table.Cell().Element(BodyCell).Text(row[0]).FontSize(TableFontSize);
                table.Cell().Element(BodyCell).Text(row[1]).FontSize(TableFontSize);
                table.Cell().Element(BodyCell).Text(row[2]).FontSize(TableTextFontSize).FontColor(Colors.Grey.Darken1);
            }
        });
    }

    /// <summary>
    /// 通用数据表。<paramref name="widths"/> 给的是相对宽度权重；<paramref name="severityColumn"/> 指定哪一列按文字上色
    /// （结论表的严重度列、能力表的可用性列）。QuestPDF 的表格会在跨页时自动分页并重复表头。
    /// </summary>
    private static void DataTable(
        ColumnDescriptor column,
        IReadOnlyList<string> headers,
        IReadOnlyList<int> widths,
        IReadOnlyList<string[]> rows,
        int severityColumn = -1)
    {
        column.Item().PaddingTop(6).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < headers.Count; i++)
                {
                    columns.RelativeColumn(widths[i]);
                }
            });

            table.Header(header =>
            {
                foreach (var title in headers)
                {
                    header.Cell().Element(HeaderCell).Text(title).FontSize(TableFontSize).Bold();
                }
            });

            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    var cell = table.Cell().Element(BodyCell);
                    var text = cell.Text(row[i]).FontSize(TableFontSize);
                    if (i == severityColumn)
                    {
                        text.FontColor(SeverityTextColor(row[i]));
                    }
                }
            }
        });
    }

    private static IContainer HeaderCell(IContainer container) => container
        .Background(Colors.Grey.Lighten2)
        .BorderBottom(1)
        .BorderColor(Colors.Grey.Medium)
        .PaddingVertical(3)
        .PaddingHorizontal(4);

    private static IContainer BodyCell(IContainer container) => container
        .BorderBottom(0.5f)
        .BorderColor(Colors.Grey.Lighten2)
        .PaddingVertical(3)
        .PaddingHorizontal(4);

    private static void AppendParagraph(ColumnDescriptor box, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        box.Item().PaddingTop(4).Text(text =>
        {
            text.DefaultTextStyle(style => style.FontSize(TableTextFontSize));
            text.Span(label + "：").Bold();
            text.Span(value!);
        });
    }

    private static void AppendBulletBlock(ColumnDescriptor box, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        box.Item().PaddingTop(4).Text(label + "：").FontSize(TableTextFontSize).Bold();
        foreach (var item in items)
        {
            box.Item().PaddingLeft(10).Text("· " + item).FontSize(TableTextFontSize);
        }
    }

    private static List<Finding> OrderedFindings(DiagnosisReport report) =>
        report.Findings
              .OrderByDescending(finding => SeverityRank(finding.Severity))
              .ThenBy(finding => finding.Category, StringComparer.Ordinal)
              .ToList();

    /// <summary>
    /// 契约里的 <see cref="FindingSeverity"/> 数值是 Info=0 &lt; Good=1，直接按枚举排序会把"正常"排到"提示"前面，
    /// 所以这里显式给出报告期望的展示顺序。
    /// </summary>
    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 5,
        FindingSeverity.Warning => 4,
        FindingSeverity.Minor => 3,
        FindingSeverity.Info => 2,
        FindingSeverity.Good => 1,
        _ => 0,
    };

    private static string SeverityText(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "严重",
        FindingSeverity.Warning => "警告",
        FindingSeverity.Minor => "轻微",
        FindingSeverity.Info => "提示",
        FindingSeverity.Good => "正常",
        _ => severity.ToString(),
    };

    private static string SeverityColor(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => Colors.Red.Darken2,
        FindingSeverity.Warning => Colors.Orange.Darken2,
        FindingSeverity.Minor => Colors.Amber.Darken2,
        FindingSeverity.Info => Colors.Blue.Darken1,
        _ => Colors.Green.Darken2,
    };

    private static string SeverityTextColor(string text) => text switch
    {
        "严重" => Colors.Red.Darken2,
        "警告" => Colors.Orange.Darken2,
        "轻微" => Colors.Amber.Darken2,
        "不可用" => Colors.Orange.Darken2,
        "可用" => Colors.Green.Darken2,
        _ => Colors.Grey.Darken3,
    };

    private static string BuildSeverityCounts(IReadOnlyList<Finding> findings)
    {
        var parts = new List<string>();
        foreach (var severity in new[]
                 {
                     FindingSeverity.Critical,
                     FindingSeverity.Warning,
                     FindingSeverity.Minor,
                     FindingSeverity.Info,
                     FindingSeverity.Good,
                 })
        {
            var count = findings.Count(finding => finding.Severity == severity);
            if (count > 0)
            {
                parts.Add($"{SeverityText(severity)} {count.ToString(CultureInfo.InvariantCulture)} 条");
            }
        }

        return parts.Count == 0 ? "无" : string.Join("、", parts);
    }

    private static bool IsSelfTest(DiagnosisReport report) =>
        report.Session.Warnings.Any(warning =>
            warning.Contains(SelfTest.SelfTestMarker, StringComparison.Ordinal)) ||
        report.Session.Samples.Any(sample => sample.Notes.Any(note =>
            note.Contains(SelfTest.SelfTestMarker, StringComparison.Ordinal)));

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? ValueFormat.NoData : value!.Trim();

    private static string TryGetFileSize(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length.ToString(CultureInfo.InvariantCulture) + " 字节";
        }
        catch (IOException)
        {
            return "大小未知";
        }
        catch (UnauthorizedAccessException)
        {
            return "大小未知";
        }
    }

    private static string Text(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : ValueFormat.NoData;

    private static string BoolText(bool? value) => value switch
    {
        true => "已开启",
        false => "未开启",
        null => ValueFormat.NoData,
    };

    private static string FormatTime(DateTimeOffset value) =>
        value == default
            ? ValueFormat.NoData
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
