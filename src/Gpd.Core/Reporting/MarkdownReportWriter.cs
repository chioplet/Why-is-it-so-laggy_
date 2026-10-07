using System.IO;
using System.Text;

namespace Gpd.Core.Reporting;

/// <summary>
/// 中文 Markdown 诊断报告。九节结构：结论速览 → 硬件与系统 → 采样概况 → 汇总指标 →
/// 时间序列 → 诊断结论 → 画质解读 → 逐核快照 → 数据来源与可信度。
/// </summary>
public sealed class MarkdownReportWriter : IReportWriter
{
    /// <summary>时间序列表最多输出多少行，超出则等间隔抽样并注明抽样步长。</summary>
    public const int MaxSeriesRows = 200;

    public string Write(DiagnosisReport report, string outputDirectory, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);

        var directory = ReportFileHelper.EnsureDirectory(outputDirectory);
        var path = Path.Combine(directory, ReportFileHelper.BuildFileName(report, string.Empty, "md"));
        File.WriteAllText(path, Build(report, directory), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return Path.GetFullPath(path);
    }

    /// <summary>渲染成 Markdown 文本（便于自测与单测直接检查内容）。</summary>
    public string Build(DiagnosisReport report, string? outputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(16 * 1024);

        sb.Append("# 游戏性能诊断报告").Append('\n').Append('\n');
        sb.Append("生成时间：").Append(report.GeneratedAt == default ? "未知" : report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))
          .Append("　　工具版本：").Append(string.IsNullOrWhiteSpace(report.ToolVersion) ? "未知" : report.ToolVersion)
          .Append('\n').Append('\n');

        AppendVerdict(sb, report);
        AppendMachine(sb, report);
        AppendSession(sb, report);
        AppendSummary(sb, report);
        AppendTimeline(sb, report);
        AppendFindings(sb, report);
        AppendGraphics(sb, report);
        AppendCores(sb, report);
        AppendTrust(sb, report, outputDirectory);

        sb.Append('\n').Append("---").Append('\n').Append('\n');
        sb.Append("> 本报告由 wiisl 根据本机采集到的真实计数自动生成，未做任何人工润色。")
          .Append("所有百分比与频率均来自采样平均值/最大值，孤立的瞬时波动不构成结论。").Append('\n');

        return sb.ToString();
    }

    // ── 一、结论速览 ────────────────────────────────────────────────────────────

    private static void AppendVerdict(StringBuilder sb, DiagnosisReport report)
    {
        sb.Append("## 一、结论速览").Append('\n').Append('\n');

        var verdict = string.IsNullOrWhiteSpace(report.Summary.BottleneckVerdict)
            ? "未知"
            : report.Summary.BottleneckVerdict;

        sb.Append("**瓶颈判定：").Append(DescribeVerdict(verdict)).Append("**").Append('\n').Append('\n');

        if (report.Findings.Count == 0)
        {
            sb.Append("本次分析没有产生任何结论（可能采样为空）。").Append('\n').Append('\n');
            return;
        }

        sb.Append("| 严重度 | 结论 | 类别 |").Append('\n');
        sb.Append("| --- | --- | --- |").Append('\n');
        foreach (var finding in report.Findings)
        {
            sb.Append("| ").Append(SeverityText(finding.Severity))
              .Append(" | ").Append(Escape(Text(finding.Title)))
              .Append(" | ").Append(Escape(Text(finding.Category)))
              .Append(" |").Append('\n');
        }

        sb.Append('\n');

        // 只有真正"需要处理"的结论（警告以上）才配当"最该先做的一件事"。
        // 否则全是提示/正常时，会把"装个 HWiNFO 才能读到 CPU 封装温度"这类采集能力说明
        // 推到第一位，看起来像在命令用户去装软件。
        var actionable = report.Findings
            .Where(f => f.Recommendations is { Count: > 0 } && SeverityRank(f.Severity) >= SeverityRank(FindingSeverity.Warning))
            .OrderByDescending(f => SeverityRank(f.Severity))
            .FirstOrDefault();

        sb.Append(actionable is not null
            ? "> **最该先做的一件事：**" + Escape(actionable.Recommendations[0])
            : "> **本次采样没有发现需要立即处理的问题。**想拿到更完整的结论，可以按「九、数据来源与可信度」一节补齐受限项后重测。")
          .Append('\n').Append('\n');
    }

    private static string DescribeVerdict(string verdict) => verdict switch
    {
        "GPU" => "GPU 瓶颈（显卡接近满载，CPU 还有余量）",
        "CPU" => "CPU 瓶颈（CPU 接近满载，显卡还没吃满）",
        "均衡" => "没有明显的单一瓶颈（CPU 与 GPU 负载相对均衡）",
        _ => verdict + "（本次采集不足以判定瓶颈）",
    };

    // ── 二、硬件与系统 ──────────────────────────────────────────────────────────

    private static void AppendMachine(StringBuilder sb, DiagnosisReport report)
    {
        var machine = report.Session.Machine;
        sb.Append("## 二、硬件与系统").Append('\n').Append('\n');

        if (machine is null)
        {
            sb.Append("本次没有采集到硬件与系统信息。").Append('\n').Append('\n');
            return;
        }

        sb.Append("| 项目 | 值 |").Append('\n');
        sb.Append("| --- | --- |").Append('\n');
        Row(sb, "操作系统", $"{Text(machine.OsName)} {Text(machine.OsVersion)}");
        Row(sb, "系统版本号", Text(machine.OsBuild));
        Row(sb, "CPU", Text(machine.CpuName));
        Row(sb, "物理核心 / 逻辑处理器", $"{machine.CpuPhysicalCores} / {machine.CpuLogicalProcessors}");
        Row(sb, "CPU 标称频率", ValueFormat.Mhz(machine.CpuNominalMhz));
        Row(sb, "物理内存", ValueFormat.Memory(machine.TotalMemoryMb));
        Row(sb, "电源计划", Text(machine.PowerPlanName));
        Row(sb, "游戏模式", machine.GameModeEnabled.HasValue ? (machine.GameModeEnabled.Value ? "已开启" : "未开启") : ValueFormat.NoData);
        Row(sb, "硬件加速 GPU 计划", machine.HardwareGpuSchedulingEnabled.HasValue
            ? (machine.HardwareGpuSchedulingEnabled.Value ? "已开启" : "未开启")
            : ValueFormat.NoData);
        Row(sb, "NVIDIA 驱动", Text(machine.NvidiaDriverVersion));
        Row(sb, "以管理员身份运行", machine.IsElevated ? "是" : "否");
        sb.Append('\n');

        if (machine.Gpus is { Count: > 0 })
        {
            sb.Append("### 显卡").Append('\n').Append('\n');
            sb.Append("| # | 显卡 | 显存 | 驱动 | 虚拟显示器 |").Append('\n');
            sb.Append("| --- | --- | --- | --- | --- |").Append('\n');
            for (var i = 0; i < machine.Gpus.Count; i++)
            {
                var gpu = machine.Gpus[i];
                sb.Append("| ").Append(i + 1)
                  .Append(" | ").Append(Escape(Text(gpu.Name)))
                  .Append(" | ").Append(ValueFormat.Memory(gpu.VramMb))
                  .Append(" | ").Append(Escape(Text(gpu.DriverVersion)))
                  .Append(" | ").Append(gpu.IsVirtualDisplayAdapter ? "是" : "否")
                  .Append(" |").Append('\n');
            }

            sb.Append('\n');
        }
    }

    // ── 三、采样概况 ────────────────────────────────────────────────────────────

    private static void AppendSession(StringBuilder sb, DiagnosisReport report)
    {
        var session = report.Session;
        sb.Append("## 三、采样概况").Append('\n').Append('\n');

        sb.Append("| 项目 | 值 |").Append('\n');
        sb.Append("| --- | --- |").Append('\n');
        Row(sb, "目标进程", $"{Text(session.ProcessName)}（PID {session.ProcessId}）");
        Row(sb, "进程路径", Text(session.ProcessPath));
        Row(sb, "采样时长", ValueFormat.Duration(report.Summary.DurationSeconds));
        Row(sb, "采样点数", report.Summary.SampleCount.ToString());
        Row(sb, "采样间隔", ValueFormat.Number(session.IntervalSeconds, 2) + " 秒");
        Row(sb, "有效帧率样本", session.FpsSampleCount.ToString());
        Row(sb, "开始时间", session.StartedAt == default ? ValueFormat.NoData : session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Row(sb, "结束时间", session.EndedAt == default
            ? ValueFormat.NoData
            : session.EndedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Row(sb, "停止原因", DescribeStopReason(session.StopReason));
        sb.Append('\n');

        if (session.Game is not null)
        {
            var game = session.Game;
            sb.Append("### 识别到的游戏").Append('\n').Append('\n');
            sb.Append("| 项目 | 值 |").Append('\n');
            sb.Append("| --- | --- |").Append('\n');
            Row(sb, "名称", Text(game.Name));
            Row(sb, "平台", Text(game.PlatformName));
            Row(sb, "平台 AppId", Text(game.AppId));
            Row(sb, "安装目录", Text(game.InstallDir));
            Row(sb, "主程序", Text(game.ExecutablePath));
            Row(sb, "识别来源", Text(game.DiscoverySource));
            Row(sb, "最近运行", game.LastPlayed.HasValue ? game.LastPlayed.Value.ToString("yyyy-MM-dd HH:mm:ss") : ValueFormat.NoData);
            Row(sb, "累计时长", game.PlayTime.HasValue ? ValueFormat.Number(game.PlayTime.Value.TotalHours, 1) + " 小时" : ValueFormat.NoData);
            Row(sb, "配置文件", (game.ConfigFiles?.Count ?? 0) + " 个");
            sb.Append('\n');
        }
    }

    // ── 四、汇总指标 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 停止原因转中文。<see cref="SampleSession.StopReason"/> 是枚举名的英文字符串
    /// （Manual / Duration / ProcessExited / Error），直接写进中文报告会不协调。
    /// </summary>
    public static string DescribeStopReason(string? raw) => raw switch
    {
        "Manual" => "手动停止",
        "Duration" => "到达设定时长",
        "ProcessExited" => "目标进程已退出",
        "Error" => "异常终止",
        null or "" => "本机采集不到",
        _ => raw,
    };

    private static void AppendSummary(StringBuilder sb, DiagnosisReport report)
    {
        var s = report.Summary;
        sb.Append("## 四、汇总指标").Append('\n').Append('\n');
        sb.Append("| 指标 | 平均 | 最大 / 峰值 | 说明 |").Append('\n');
        sb.Append("| --- | --- | --- | --- |").Append('\n');
        Row3(sb, "CPU 总占用", ValueFormat.Percent(s.CpuAvgPercent), ValueFormat.Percent(s.CpuMaxPercent), "任务管理器口径的整机 CPU 占用");
        Row3(sb, "CPU 频率", ValueFormat.Mhz(s.CpuAvgFrequencyMhz), ValueFormat.Mhz(s.CpuMaxFrequencyMhz), "标称频率之上说明在睿频");
        Row3(sb, s.CpuTempTracksLoad == true ? "CPU 温度" : "热区温度",
             ValueFormat.Temp(s.CpuAvgTempC), ValueFormat.Temp(s.CpuMaxTempC),
             s.CpuTempTracksLoad == true
                 ? "持续 90°C 以上会触发降频"
                 : "ACPI 主板热区，未验证为 CPU 封装温度，不要据此判断 CPU 是否过热（见结论速览）");
        Row3(sb, "CPU 封装功耗", ValueFormat.Watt(s.CpuAvgPowerW), ValueFormat.Watt(s.CpuMaxPowerW), "受 PL1/PL2 限制");
        Row3(sb, "GPU 占用", ValueFormat.Percent(s.GpuAvgUtilPercent), ValueFormat.Percent(s.GpuMaxUtilPercent), "接近 100% 说明显卡是瓶颈");
        Row3(sb, "GPU 核心频率", ValueFormat.Mhz(s.GpuAvgClockMhz), ValueFormat.Mhz(s.GpuMaxClockMhz), "掉频通常来自功耗墙或温度墙");
        Row3(sb, "GPU 温度", ValueFormat.Temp(s.GpuAvgTempC), ValueFormat.Temp(s.GpuMaxTempC), "83°C 起进入风险区");
        Row3(sb, "GPU 功耗", ValueFormat.Watt(s.GpuAvgPowerW), ValueFormat.Watt(s.GpuMaxPowerW), $"功耗上限 {ValueFormat.Watt(s.GpuPowerLimitW)}");
        Row3(sb, "进程工作集内存", ValueFormat.Memory(s.ProcessAvgWorkingSetMb), ValueFormat.Memory(s.ProcessMaxWorkingSetMb), "游戏进程实际占用的物理内存");
        Row3(sb, "系统可用内存", ValueFormat.Memory(s.SystemMinAvailableMemoryMb), "—", "整段采样里的最低可用内存，比平均值更能说明问题");
        Row3(sb, "页面文件占用", "—", ValueFormat.Percent(s.PageFileMaxPercent), "长期高位说明物理内存已经不够");
        Row3(sb, "磁盘活动时间", ValueFormat.Percent(s.DiskAvgPercent), ValueFormat.Percent(s.DiskMaxPercent), "读盘造成的卡顿会同时抬高队列长度");
        Row3(sb, "磁盘队列长度", "—", ValueFormat.Number(s.DiskMaxQueueLength, 2), "持续大于 2 说明磁盘是瓶颈");
        Row3(sb, "帧率 FPS", ValueFormat.Fps(s.FpsAvg), ValueFormat.Fps(s.FpsMax), $"最低 {ValueFormat.Fps(s.FpsMin)}");
        Row3(sb, "1% Low", ValueFormat.Fps(s.Fps1PercentLow), "—", "最能反映主观卡顿的指标");
        Row3(sb, "0.1% Low", ValueFormat.Fps(s.Fps01PercentLow), "—", "极端卡顿帧");
        Row3(sb, "帧时间 P99", "—", ValueFormat.Ms(s.FrameTimeP99Ms), "99% 的帧都快于这个时长");
        Row3(sb, "帧时间标准差", "—", ValueFormat.Ms(s.FrameTimeStdDevMs), "标准差越大帧生成越不稳定");
        Row3(sb, "卡顿帧次数", "—", s.StutterCount.ToString(), "帧时间超过中位数 2 倍的帧数");
        Row3(sb, "卡顿频率", "—", s.StuttersPerMinute.HasValue ? ValueFormat.Number(s.StuttersPerMinute, 2) + " 次/分钟" : ValueFormat.NoData, "每分钟卡顿帧次数");

        sb.Append('\n');
        sb.Append("- GPU 功耗触顶样本占比：").Append(ValueFormat.Percent(s.GpuPowerLimitedPercent)).Append("（功耗 ≥ 上限 95% 的样本）").Append('\n');
        sb.Append("- GPU 高温样本占比：").Append(ValueFormat.Percent(s.GpuThermalRiskPercent)).Append("（温度 ≥ 83°C 的样本）").Append('\n');
        sb.Append("- CPU 与 GPU 占用差距：").Append(ValueFormat.Percent(s.CpuGpuGapPercent)).Append("（两者平均占用之差的绝对值）").Append('\n');
        sb.Append("- 瓶颈判定：").Append(DescribeVerdict(string.IsNullOrWhiteSpace(s.BottleneckVerdict) ? "未知" : s.BottleneckVerdict)).Append('\n').Append('\n');
    }

    // ── 五、时间序列 ────────────────────────────────────────────────────────────

    private static void AppendTimeline(StringBuilder sb, DiagnosisReport report)
    {
        var samples = report.Session.Samples;
        sb.Append("## 五、时间序列").Append('\n').Append('\n');

        if (samples is null || samples.Count == 0)
        {
            sb.Append("本次没有采集到任何采样点。").Append('\n').Append('\n');
            return;
        }

        var step = samples.Count > MaxSeriesRows ? (int)Math.Ceiling(samples.Count / (double)MaxSeriesRows) : 1;
        if (step > 1)
        {
            sb.Append("采样点共 ").Append(samples.Count).Append(" 个，超过 ").Append(MaxSeriesRows)
              .Append(" 行，下面每 ").Append(step).Append(" 个采样点取 1 个（首末点保留）。").Append('\n').Append('\n');
        }

        sb.Append("| 时间(s) | CPU% | CPU频率 | CPU温度 | CPU功耗 | GPU% | GPU频率 | GPU温度 | GPU功耗 | 进程内存 | 可用内存 | FPS | 帧时间(ms) |").Append('\n');
        sb.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |").Append('\n');

        for (var i = 0; i < samples.Count; i++)
        {
            if (step > 1 && i % step != 0 && i != samples.Count - 1)
            {
                continue;
            }

            var sample = samples[i];
            sb.Append("| ").Append(ValueFormat.Number(sample.ElapsedSeconds, 1))
              .Append(" | ").Append(ValueFormat.Percent(sample.CpuTotalPercent))
              .Append(" | ").Append(ValueFormat.Mhz(sample.CpuFrequencyMhz))
              .Append(" | ").Append(ValueFormat.Temp(sample.CpuPackageTempC))
              .Append(" | ").Append(ValueFormat.Watt(sample.CpuPackagePowerW))
              .Append(" | ").Append(ValueFormat.Percent(sample.GpuUtilPercent))
              .Append(" | ").Append(ValueFormat.Mhz(sample.GpuClockMhz))
              .Append(" | ").Append(ValueFormat.Temp(sample.GpuTempC))
              .Append(" | ").Append(ValueFormat.Watt(sample.GpuPowerW))
              .Append(" | ").Append(ValueFormat.Memory(sample.ProcessWorkingSetMb))
              .Append(" | ").Append(ValueFormat.Memory(sample.SystemAvailableMemoryMb))
              .Append(" | ").Append(ValueFormat.Fps(sample.Fps))
              .Append(" | ").Append(ValueFormat.Ms(sample.FrameTimeMs))
              .Append(" |").Append('\n');
        }

        sb.Append('\n');
    }

    // ── 六、诊断结论 ────────────────────────────────────────────────────────────

    private static void AppendFindings(StringBuilder sb, DiagnosisReport report)
    {
        sb.Append("## 六、诊断结论").Append('\n').Append('\n');

        if (report.Findings is null || report.Findings.Count == 0)
        {
            sb.Append("本次没有产生任何结论。").Append('\n').Append('\n');
            return;
        }

        var index = 0;
        foreach (var finding in report.Findings)
        {
            index++;
            sb.Append("### ").Append(index).Append(".【").Append(SeverityText(finding.Severity)).Append("】")
              .Append(Escape(Text(finding.Title))).Append('\n').Append('\n');

            sb.Append("- **类别**：").Append(Escape(Text(finding.Category))).Append('\n');

            if (finding.Evidence is { Count: > 0 })
            {
                sb.Append("- **实测依据**：").Append('\n');
                foreach (var item in finding.Evidence)
                {
                    sb.Append("  - ").Append(Escape(item)).Append('\n');
                }
            }
            else
            {
                sb.Append("- **实测依据**：本次没有采集到能支撑该结论的数据").Append('\n');
            }

            sb.Append("- **为什么会这样**：")
              .Append(string.IsNullOrWhiteSpace(finding.Explanation)
                  ? "本次没有采集到足够信息解释成因，请结合上面的实测数字判断。"
                  : Escape(finding.Explanation))
              .Append('\n');

            if (finding.Recommendations is { Count: > 0 })
            {
                sb.Append("- **建议**：").Append('\n');
                foreach (var item in finding.Recommendations)
                {
                    sb.Append("  - ").Append(Escape(item)).Append('\n');
                }
            }

            if (!string.IsNullOrWhiteSpace(finding.ExpectedGain))
            {
                sb.Append("- **预期效果**：").Append(Escape(finding.ExpectedGain)).Append('\n');
            }

            if (finding.ConfigEvidence is { Count: > 0 })
            {
                sb.Append("- **配置证据**：").Append('\n');
                foreach (var item in finding.ConfigEvidence)
                {
                    sb.Append("  - ").Append(Escape(item)).Append('\n');
                }
            }

            sb.Append('\n');
        }
    }

    // ── 七、画质解读 ────────────────────────────────────────────────────────────

    private static void AppendGraphics(StringBuilder sb, DiagnosisReport report)
    {
        sb.Append("## 七、画质解读").Append('\n').Append('\n');

        if (report.GraphicsInsights is null || report.GraphicsInsights.Count == 0)
        {
            sb.Append("没有解析到画质配置。可能原因：游戏设置文件不在已知位置、游戏本次没有写出配置文件、或游戏把设置存在了云端。").Append('\n').Append('\n');
            return;
        }

        sb.Append("| 画质项 | 当前值 | 规范值 | 性能影响(0-5) | 解读 |").Append('\n');
        sb.Append("| --- | --- | --- | --- | --- |").Append('\n');
        foreach (var insight in report.GraphicsInsights)
        {
            sb.Append("| ").Append(Escape(Text(insight.SettingName)))
              .Append(" | ").Append(Escape(Text(insight.RawValue)))
              .Append(" | ").Append(Escape(Text(insight.NormalizedValue)))
              .Append(" | ").Append(insight.PerformanceImpact.ToString())
              .Append(" | ").Append(Escape(Text(insight.Comment)))
              .Append(" |").Append('\n');
        }

        sb.Append('\n');
    }

    // ── 八、逐核快照 ────────────────────────────────────────────────────────────

    private static void AppendCores(StringBuilder sb, DiagnosisReport report)
    {
        sb.Append("## 八、逐核快照").Append('\n').Append('\n');

        if (report.Cores is null || report.Cores.Count == 0)
        {
            sb.Append("没有采集到逐逻辑核心数据。").Append('\n').Append('\n');
            return;
        }

        sb.Append("| 核心 | 平均占用 | 最高温度 |").Append('\n');
        sb.Append("| --- | --- | --- |").Append('\n');
        foreach (var core in report.Cores)
        {
            sb.Append("| ").Append(Escape(Text(core.Label)))
              .Append(" | ").Append(ValueFormat.Percent(core.UsagePercent))
              .Append(" | ").Append(ValueFormat.Temp(core.TempC))
              .Append(" |").Append('\n');
        }

        sb.Append('\n');
    }

    // ── 九、数据来源与可信度 ────────────────────────────────────────────────────

    private static void AppendTrust(StringBuilder sb, DiagnosisReport report, string? outputDirectory)
    {
        sb.Append("## 九、数据来源与可信度").Append('\n').Append('\n');

        var machine = report.Session.Machine;
        if (machine?.Capabilities is { Count: > 0 })
        {
            sb.Append("| 数据项 | 是否可用 | 说明 |").Append('\n');
            sb.Append("| --- | --- | --- |").Append('\n');
            foreach (var capability in machine.Capabilities)
            {
                sb.Append("| ").Append(Escape(Text(capability.Name)))
                  .Append(" | ").Append(capability.Available ? "可用" : "不可用")
                  .Append(" | ").Append(Escape(Text(capability.Detail)))
                  .Append(" |").Append('\n');
            }

            sb.Append('\n');
        }
        else
        {
            sb.Append("本次没有采集到数据源可用性清单。").Append('\n').Append('\n');
        }

        var warnings = report.Session.Warnings;
        sb.Append("### 采集过程中的告警").Append('\n').Append('\n');
        if (warnings is { Count: > 0 })
        {
            foreach (var warning in warnings)
            {
                sb.Append("- ").Append(Escape(warning)).Append('\n');
            }
        }
        else
        {
            sb.Append("- 无").Append('\n');
        }

        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            sb.Append("### 附：原始数据文件").Append('\n').Append('\n');
            var files = ReportFileHelper.ListCompanionFiles(outputDirectory!, report);
            if (files.Count == 0)
            {
                sb.Append("- 尚未写出其它文件").Append('\n');
            }
            else
            {
                foreach (var file in files)
                {
                    long size;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                        size = -1;
                    }

                    sb.Append("- ").Append(Path.GetFileName(file))
                      .Append("（").Append(size >= 0 ? size.ToString() : "未知").Append(" 字节）").Append('\n');
                }
            }

            sb.Append('\n');
        }
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────────

    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 5,
        FindingSeverity.Warning => 4,
        FindingSeverity.Minor => 3,
        FindingSeverity.Info => 2,
        _ => 1,
    };

    private static string SeverityText(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "严重",
        FindingSeverity.Warning => "警告",
        FindingSeverity.Minor => "轻微",
        FindingSeverity.Info => "提示",
        FindingSeverity.Good => "良好",
        _ => "提示",
    };

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? ValueFormat.NoData : value.Trim();

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("|", "\\|", StringComparison.Ordinal)
                    .Replace("\r\n", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Replace("\r", " ", StringComparison.Ordinal);
    }

    private static void Row(StringBuilder sb, string name, string? value)
    {
        sb.Append("| ").Append(Escape(name)).Append(" | ").Append(Escape(value)).Append(" |").Append('\n');
    }

    private static void Row3(StringBuilder sb, string name, string average, string peak, string note)
    {
        sb.Append("| ").Append(Escape(name))
          .Append(" | ").Append(Escape(average))
          .Append(" | ").Append(Escape(peak))
          .Append(" | ").Append(Escape(note))
          .Append(" |").Append('\n');
    }
}
