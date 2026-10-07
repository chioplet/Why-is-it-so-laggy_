using System.Text.RegularExpressions;
using Gpd.Core.Reporting;

namespace Gpd.Core.Analysis;

/// <summary>
/// 诊断引擎：把统计摘要 + 配置解读 + 能力探测结果，翻译成人能看懂、能照着做的结论。
/// <para>
/// 【两条硬规则】
/// 1. 每条结论都必须带 <see cref="Finding.Evidence"/> 实测数字，数字只能来自 <see cref="DiagnosisReport"/> 里已有的实测值。
/// 2. 采不到的数据不许猜：要么不输出该条结论，要么明确写"本机采集不到 X，无法判断"。
/// </para>
/// </summary>
public static class DiagnosisEngine
{
    // ---- 判定阈值集中在此，便于审阅。全部为经验参考线，不是标准值。----
    private const double GpuSaturated = 90.0;      // GPU 接近满载
    private const double CpuSaturated = 80.0;      // CPU 接近满载
    private const double CpuIdleRoom = 70.0;       // CPU 有明显余量
    private const double GpuRoom = 80.0;           // GPU 有明显余量
    private const double FpsSmoothReference = 60.0; // 流畅参考线（本机没有显示器刷新率数据，统一用 60）
    private const double SingleCoreSaturated = 90.0; // 单核吃满
    private const double GpuTempWarn = 83.0;       // 移动端 GPU 降频高发温度
    private const double GpuTempCritical = 87.0;
    private const double CpuTempWarn = 90.0;
    private const double MemoryCriticalMb = 2048.0;
    private const double MemoryWarnMb = 4096.0;
    private const double MemoryComfortableMb = 8192.0;
    private const double CommittedWarn = 90.0;
    private const double PageFileWarn = 50.0;
    private const double DiskQueueWarn = 2.0;
    private const double DiskBusyWarn = 80.0;
    private const double FpsLowRatioWarn = 0.6;    // 1% Low / 平均 FPS
    private const double StutterPerMinuteWarn = 6.0;
    private const double BackgroundGapPercent = 25.0;
    private const double BackgroundSampleRatio = 0.30;
    private const double GraphicsImpactSumHigh = 18.0;
    private const double GpuAlmostMaxed = 95.0;

    private static readonly Regex ResolutionPattern = new(@"(?<w>\d{3,5})\s*[x×*]\s*(?<h>\d{3,5})", RegexOptions.Compiled);

    /// <summary>执行全部诊断检查，返回按严重程度降序排列的结论列表。</summary>
    public static List<Finding> Analyze(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var session = report.Session;
        var summary = report.Summary;
        var findings = new List<Finding>();

        if (session.Samples.Count < 5)
        {
            findings.Add(New(
                "采样时间过短（不足 5 秒），结论不可靠",
                FindingSeverity.Warning,
                "数据可信度",
                evidence:
                [
                    $"本次只采到 {session.Samples.Count} 个采样点（门槛为 5 个）",
                    $"采样时长 {ValueFormat.Duration(summary.DurationSeconds)}",
                ],
                explanation:
                "几秒钟的数据只能反映游戏刚启动、着色器还在编译、贴图还在加载的阶段，" +
                "这个阶段 CPU/GPU 占用和帧率都不稳定，拿它判断瓶颈会得出错误结论。",
                recommendations:
                [
                    "让游戏进到实际游玩的场景（跑图、战斗）后，再采 60 秒以上重跑一次。",
                    "两次报告对比同一场景，结论才可信。",
                ],
                expectedGain: "无法估计：样本不足时不给出任何性能收益承诺。",
                configEvidence: []));

            return SortFindings(findings);
        }

        CheckGpuBottleneck(summary, findings);
        CheckCpuBottleneck(session, summary, findings);
        CheckGpuPowerLimit(summary, findings);
        CheckGpuTemperature(summary, findings);
        CheckCpuTemperature(summary, findings);
        CheckMemory(summary, session, findings);
        CheckDisk(summary, findings);
        CheckFramePacing(summary, findings);
        CheckGraphicsMatch(report, summary, findings);
        CheckBackgroundLoad(session, findings);
        CheckCapabilities(session, findings);
        CheckMissingFpsData(session, summary, findings);
        CheckHealthySigns(summary, findings);

        return SortFindings(findings);
    }

    // ========================================================================
    //  1. GPU 瓶颈
    // ========================================================================
    private static void CheckGpuBottleneck(PerformanceSummary summary, List<Finding> findings)
    {
        var gpu = summary.GpuAvgUtilPercent;
        if (!gpu.HasValue || gpu.Value < GpuSaturated)
        {
            return;
        }

        var hasFps = summary.FpsAvg.HasValue;
        var fpsLow = hasFps && summary.FpsAvg!.Value < FpsSmoothReference;
        if (hasFps && !fpsLow)
        {
            // GPU 满载但帧率达标：不是问题，交给"正常项"去肯定。
            return;
        }

        findings.Add(New(
            "GPU 是瓶颈（显卡接近满载）",
            FindingSeverity.Warning,
            "性能瓶颈",
            [
                $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}，最高 {ValueFormat.Percent(summary.GpuMaxUtilPercent)}（满载参考线 {GpuSaturated:F0}%）",
                $"GPU 核心频率 平均 {ValueFormat.Mhz(summary.GpuAvgClockMhz)}，最高 {ValueFormat.Mhz(summary.GpuMaxClockMhz)}",
                $"GPU 功耗 平均 {ValueFormat.Watt(summary.GpuAvgPowerW)}（上限 {ValueFormat.Watt(summary.GpuPowerLimitW)}），温度 平均 {ValueFormat.Temp(summary.GpuAvgTempC)}",
                summary.CpuAvgPercent.HasValue
                    ? $"同期 CPU 平均占用 {ValueFormat.Percent(summary.CpuAvgPercent)}（低于 {CpuIdleRoom:F0}% 说明 CPU 还有余量，瓶颈在显卡）"
                    : "CPU 占用未采集到，无法从占用对比上排除 CPU 侧影响",
                hasFps
                    ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}"
                    : "本次没有采集到帧率数据，GPU 满载是从占用率单方面判断的",
            ],
            "GPU 占用长时间贴着 100%，说明显卡的渲染能力已经被用尽，此时帧率由显卡决定，" +
            "再优化 CPU 或内存都不会有明显提升。降低画面渲染量（分辨率、光追、阴影、体积雾）或启用超分辨率（DLSS/FSR）" +
            "是唯一能直接换回帧率的方向。",
            [
                "把「阴影质量」「体积雾/体积云」「光追」「后期处理」各降一档，一次只改一项，改完用本工具重测对比。",
                "开启 DLSS / FSR / TSR（质量档优先，不够再降性能档），这是提升帧率最直接的一项。",
                "把渲染分辨率从 100% 降到 85%~90%（分辨率缩放），画质损失小、帧率收益明显。",
                "把帧率上限设为「显示器刷新率 − 3」，让 GPU 留一点余量，减少帧时间抖动和发热。",
            ],
            "降低画质档位或开启 DLSS 后，帧率提升幅度取决于具体关掉了哪一项，本工具不做百分比承诺；" +
            "请改一项、测一次，用两次报告的平均帧率对比。",
            []));
    }

    // ========================================================================
    //  2. CPU 瓶颈 / 单线程瓶颈
    // ========================================================================
    private static void CheckCpuBottleneck(SampleSession session, PerformanceSummary summary, List<Finding> findings)
    {
        var cpu = summary.CpuAvgPercent;
        var gpu = summary.GpuAvgUtilPercent;

        if (cpu.HasValue && cpu.Value >= CpuSaturated && (!gpu.HasValue || gpu.Value < GpuRoom))
        {
            findings.Add(New(
                "CPU 是瓶颈（处理器接近满载）",
                FindingSeverity.Warning,
                "性能瓶颈",
                [
                    $"CPU 平均占用 {ValueFormat.Percent(summary.CpuAvgPercent)}，最高 {ValueFormat.Percent(summary.CpuMaxPercent)}（满载参考线 {CpuSaturated:F0}%）",
                    $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}（低于 {GpuRoom:F0}%，说明显卡没吃满，在等 CPU 出数据）",
                    $"CPU 平均频率 {ValueFormat.Mhz(summary.CpuAvgFrequencyMhz)}，平均功耗 {ValueFormat.Watt(summary.CpuAvgPowerW)}，平均温度 {ValueFormat.Temp(summary.CpuAvgTempC)}",
                    summary.FpsAvg.HasValue
                        ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}"
                        : "本次没有采集到帧率数据",
                ],
                "CPU 先于 GPU 吃满时，显卡每帧都在等 CPU 准备绘制指令（Draw Call、物理、AI、脚本），" +
                "表现是显卡占用上不去、帧率也上不去。这类瓶颈靠降 GPU 画质几乎无效，要减少 CPU 侧工作量。",
                [
                    "关掉后台程序：浏览器（尤其是多标签页/视频）、录屏、直播推流、RGB 灯控、杀毒实时扫描。",
                    "降低吃 CPU 的画质项：「视距/绘制距离」「粒子效果」「人群/单位数量」「阴影距离」「物理效果」。",
                    "在游戏里开启多线程渲染/线程优化选项（不同引擎名字不同，通常在「图形」或「高级图形」里）。",
                    "确认 Windows 电源计划为「高性能」或「卓越性能」，避免 CPU 被限频。",
                    "若 CPU 是 6 核以下的老型号，注意这类 CPU 在现代 3A 游戏中本身就会成为上限。",
                ],
                "把吃 CPU 的画质项降下来、清掉后台占用后，CPU 占用会下降，帧率提升幅度取决于具体省下了多少 CPU 时间；本工具不做百分比承诺。",
                []));
        }

        // 单线程瓶颈：某一核 > 90%，而整机 CPU 总占用并不高。
        // 很多游戏的渲染/逻辑主线程只跑在一个核上，一个核吃满就会掉帧，总占用却看着"很闲"。
        var singleCorePeak = 0.0;
        var singleCoreIndex = -1;
        var singleCoreSample = -1;
        var singleCoreTotal = 0.0;
        for (var i = 0; i < session.Samples.Count; i++)
        {
            var sample = session.Samples[i];
            if (sample.CpuPerCorePercent is null || sample.CpuPerCorePercent.Count == 0)
            {
                continue;
            }

            for (var core = 0; core < sample.CpuPerCorePercent.Count; core++)
            {
                var usage = sample.CpuPerCorePercent[core];
                if (usage > singleCorePeak)
                {
                    singleCorePeak = usage;
                    singleCoreIndex = core;
                    singleCoreSample = i;
                    singleCoreTotal = sample.CpuTotalPercent ?? 0;
                }
            }
        }

        var totalLooksIdle = !cpu.HasValue || cpu.Value < CpuSaturated;
        if (singleCoreIndex >= 0 && singleCorePeak > SingleCoreSaturated && totalLooksIdle)
        {
            findings.Add(New(
                "单线程瓶颈（一个核心先吃满）",
                FindingSeverity.Warning,
                "性能瓶颈",
                [
                    $"最高单核占用 {singleCorePeak:F1}%（逻辑处理器 #{singleCoreIndex}，出现在第 {singleCoreSample + 1} 个采样点，参考线 {SingleCoreSaturated:F0}%）",
                    $"该时刻 CPU 总占用只有 {singleCoreTotal:F1}%——整机看着不忙，但主线程那一核已经满了",
                    session.Machine.CpuLogicalProcessors > 0
                        ? $"本机共 {session.Machine.CpuLogicalProcessors} 个逻辑处理器"
                        : "逻辑处理器数量未采集到",
                    summary.FpsAvg.HasValue
                        ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps01PercentLow)}"
                        : "本次没有采集到帧率数据",
                ],
                "很多游戏把渲染主线程、物理、脚本逻辑放在同一个核上。只要这个核吃满，帧率就被它卡住，" +
                "哪怕其它核闲着、CPU 总占用只有 30% 也一样掉帧。这也是「CPU 占用不高但就是卡」最常见的原因。",
                [
                    "关掉后台单线程大户：浏览器、录屏、模拟器、带实时监控的杀毒软件。",
                    "降低「视距/绘制距离」「粒子数量」「AI 单位数」「物理效果」这类由主线程驱动的项目。",
                    "在游戏里开启多线程渲染（如 DX12/Vulkan 模式、线程优化开关）。",
                    "在任务管理器里把游戏进程优先级设为「高」（不要设「实时」，可能让系统卡死）。",
                ],
                "主线程负载下降后，帧率上限会随之抬高；具体幅度取决于游戏的主线程实现，本工具不做百分比承诺。",
                []));
        }
    }

    // ========================================================================
    //  3. GPU 功耗墙
    // ========================================================================
    private static void CheckGpuPowerLimit(PerformanceSummary summary, List<Finding> findings)
    {
        var limited = summary.GpuPowerLimitedPercent;
        if (!limited.HasValue || limited.Value < 30)
        {
            return;
        }

        findings.Add(New(
            "GPU 受功耗墙限制（频率被压住）",
            limited.Value >= 50 ? FindingSeverity.Warning : FindingSeverity.Info,
            "功耗与散热",
            [
                $"有 {limited.Value:F1}% 的样本里 GPU 实际功耗达到了功耗上限的 95% 以上（判定线 30%）",
                $"GPU 平均功耗 {ValueFormat.Watt(summary.GpuAvgPowerW)}，功耗上限（平均）{ValueFormat.Watt(summary.GpuPowerLimitW)}",
                $"GPU 平均核心频率 {ValueFormat.Mhz(summary.GpuAvgClockMhz)}，温度 平均 {ValueFormat.Temp(summary.GpuAvgTempC)}",
                $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}",
            ],
            "显卡的功耗上限（Power Limit）决定了它能跑多高的频率。一旦功耗顶到上限，" +
            "驱动就会把核心频率压下来，表现为占用率 100% 但帧率不涨。笔记本 GPU（例如 RTX 4060 Laptop 常见的 60–115W " +
            "动态功耗区间）尤其明显：不插电、省电模式、或厂商的电池保护策略都会把上限压得更低。",
            [
                "确保笔记本已插上原装电源适配器；仅用电池时 GPU 功耗上限会被大幅压低。",
                "NVIDIA 控制面板 →「管理 3D 设置」→「电源管理模式」设为「最高性能优先」。",
                "Windows 设置 →「电源和电池」→ 电源模式选「最佳性能」；台式机可在控制面板里选「高性能/卓越性能」计划。",
                "关闭厂商控制中心（Armoury Crate / Lenovo Vantage / Omen Gaming Hub 等）里的省电、静音、电池保养限频模式。",
            ],
            "解除功耗限制后 GPU 频率能跑更高，帧率提升幅度与机型散热能力相关，本工具不做百分比承诺。",
            []));
    }

    // ========================================================================
    //  4. GPU 温度
    // ========================================================================
    private static void CheckGpuTemperature(PerformanceSummary summary, List<Finding> findings)
    {
        var maxTemp = summary.GpuMaxTempC;
        if (!maxTemp.HasValue || maxTemp.Value < GpuTempWarn)
        {
            return;
        }

        findings.Add(New(
            maxTemp.Value >= GpuTempCritical ? "GPU 温度过高，必然降频" : "GPU 温度偏高，存在降频风险",
            maxTemp.Value >= GpuTempCritical ? FindingSeverity.Critical : FindingSeverity.Warning,
            "功耗与散热",
            [
                $"GPU 最高温度 {ValueFormat.Temp(summary.GpuMaxTempC)}（警告线 {GpuTempWarn:F0}℃，严重线 {GpuTempCritical:F0}℃）",
                $"GPU 平均温度 {ValueFormat.Temp(summary.GpuAvgTempC)}；达到 {GpuTempWarn:F0}℃ 以上的样本占 {ValueFormat.Percent(summary.GpuThermalRiskPercent)}",
                $"GPU 平均频率 {ValueFormat.Mhz(summary.GpuAvgClockMhz)}，平均功耗 {ValueFormat.Watt(summary.GpuAvgPowerW)}，平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}",
                summary.FpsAvg.HasValue ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}" : "本次没有采集到帧率数据",
            ],
            "显卡核心到 80℃ 以上就会开始按温度降频（Thermal Throttling）：先降频率、再降电压，" +
            "帧率随之缓慢下滑——这种下滑在长时间游戏里特别明显，表现为「刚进游戏很流畅，十分钟后开始掉帧」。",
            [
                "笔记本垫高机身后部或使用散热底座，保证进风口不被桌面/床面挡住。",
                "清理风扇和散热鳍片的积灰（使用一年以上建议清灰 + 换硅脂）。",
                "用「限制帧率」降低 GPU 负载（例如锁 60），温度通常会明显下降。",
                "进阶（系统级、可逆）：用 `nvidia-smi -pl <瓦数>` 略微降低功耗上限，用少量性能换稳定温度；" +
                "想恢复用 `nvidia-smi -pl <原始瓦数>`，原始上限可用 `nvidia-smi -q -d POWER` 查看。",
            ],
            "散热改善后温度降到降频线以下，帧率会回到满血水平；具体提升幅度取决于当前降频了多少，本工具不做百分比承诺。",
            []));
    }

    // ========================================================================
    //  5. CPU 温度
    // ========================================================================
    private static void CheckCpuTemperature(PerformanceSummary summary, List<Finding> findings)
    {
        // 温度读数没被验证为"跟着负载走"时，它可能只是主板/机壳热区，不能拿它给 CPU 下过热结论。
        // 实测本机 ACPI 热区 \_TZ.TZ0 空闲 85.1℃、16 线程满载 82.7℃——满载反而更低，明显不是 CPU 封装温度。
        // 这时既不发 Warning 也不发 Good，改为一条说明，避免在错误的传感器上给出建议。
        if (summary.CpuMaxTempC.HasValue && summary.CpuTempTracksLoad != true)
        {
            findings.Add(New(
                "CPU 封装温度不可用，未做 CPU 过热判断",
                FindingSeverity.Info,
                "功耗与散热",
                [
                    $"读到的热区最高温度 {ValueFormat.Temp(summary.CpuMaxTempC)}，平均 {ValueFormat.Temp(summary.CpuAvgTempC)}",
                    $"负载相关性判定：{(summary.CpuTempTracksLoad == false ? "温度不随负载上升（说明它不是 CPU 封装温度）" : "本次负载跨度不足，无法判定")}",
                    $"对照数据：CPU 平均占用 {ValueFormat.Percent(summary.CpuAvgPercent)}，平均功耗 {ValueFormat.Watt(summary.CpuAvgPowerW)}",
                ],
                "本机通过 ACPI 热区读温度。ACPI 热区是主板传感器，不保证等于 CPU 封装（Package）温度：" +
                "判断 CPU 是否撞温度墙必须看封装温度。本工具检测到该读数与 CPU 负载不相关，" +
                "所以报告里不出现「CPU 温度正常/过高」这类结论——宁可说不知道，也不在错的传感器上下判断。",
                [
                    "想要准确的 CPU 封装温度，可以装 HWiNFO64 之类的工具，它通过 Intel/AMD 的 MSR 或驱动读取 " +
                    "CPU 内部数字温度传感器（DTS），和主板热区不是一回事。",
                    "注意：本工具出于「绝不改游戏文件、也不装内核驱动」的定位，不会自行安装这类驱动级传感器。",
                    "在拿到可信温度前，可以用间接信号判断 CPU 是否受限：" +
                    "看满载时频率是否明显掉到基频附近，以及功耗是否长期贴在某个固定值上。",
                ],
                "无：这是一条采集能力说明，不影响本次其它结论。",
                []));
            return;
        }

        var maxTemp = summary.CpuMaxTempC;
        if (!maxTemp.HasValue || maxTemp.Value < CpuTempWarn)
        {
            return;
        }

        findings.Add(New(
            "CPU 温度偏高",
            FindingSeverity.Warning,
            "功耗与散热",
            [
                $"CPU 封装最高温度 {ValueFormat.Temp(summary.CpuMaxTempC)}（警告线 {CpuTempWarn:F0}℃）",
                $"CPU 平均温度 {ValueFormat.Temp(summary.CpuAvgTempC)}，平均功耗 {ValueFormat.Watt(summary.CpuAvgPowerW)}，最高功耗 {ValueFormat.Watt(summary.CpuMaxPowerW)}",
                $"CPU 平均占用 {ValueFormat.Percent(summary.CpuAvgPercent)}，平均频率 {ValueFormat.Mhz(summary.CpuAvgFrequencyMhz)}",
            ],
            "CPU 到 90℃ 以上会触发温度保护：先降睿频、再降基频，直接后果是帧率波动变大、" +
            "低帧（1% Low）明显变差。笔记本上 CPU 和 GPU 往往共用散热模组，CPU 发热还会顺带把 GPU 一起拖热。",
            [
                "笔记本垫高/散热底座，确保进风口通畅；清灰换硅脂。",
                "在厂商控制中心里把性能模式设为「平衡/性能」，避免长期「静音模式」下的高温限频；" +
                "如果温度实在压不住，主动把 CPU 最大处理器状态设为 95%（控制面板 → 电源选项 → 高级设置），" +
                "用极小的性能代价换取不再撞温度墙。",
                "关掉后台持续占用 CPU 的程序（浏览器、录屏、编译器、杀毒全盘扫描）。",
            ],
            "温度降到保护线以下后，CPU 能维持更高睿频，帧率波动会变小；本工具不做百分比承诺。",
            []));
    }

    // ========================================================================
    //  6. 内存不足
    // ========================================================================
    private static void CheckMemory(PerformanceSummary summary, SampleSession session, List<Finding> findings)
    {
        var minAvailable = summary.SystemMinAvailableMemoryMb;
        if (minAvailable.HasValue && minAvailable.Value < MemoryWarnMb)
        {
            var critical = minAvailable.Value < MemoryCriticalMb;
            findings.Add(New(
                critical ? "系统可用内存严重不足" : "系统可用内存不足",
                critical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "内存",
                [
                    $"采样期间系统可用物理内存最低只剩 {ValueFormat.Memory(summary.SystemMinAvailableMemoryMb)}（严重线 {MemoryCriticalMb:F0} MB / 警告线 {MemoryWarnMb:F0} MB）",
                    session.Machine.TotalMemoryMb > 0 ? $"本机物理内存总量 {ValueFormat.Memory(session.Machine.TotalMemoryMb)}" : "物理内存总量未采集到",
                    $"目标进程工作集 平均 {ValueFormat.Memory(summary.ProcessAvgWorkingSetMb)}，最高 {ValueFormat.Memory(summary.ProcessMaxWorkingSetMb)}",
                    $"页面文件占用最高 {ValueFormat.Percent(summary.PageFileMaxPercent)}",
                ],
                "可用内存见底时，Windows 会把不常用的内存页写到页面文件（硬盘）上。" +
                "硬盘的随机读写比内存慢几个数量级，一旦发生换页，游戏就会出现几秒一卡的顿挫、" +
                "切换场景/打开背包时的长时间卡顿，严重时贴图加载不全。",
                [
                    "关掉浏览器（尤其是几十个标签页）、聊天软件、录屏/直播、虚拟机等内存大户。",
                    "把游戏的「纹理质量」降一档：高纹理是显存和内存的双重大户。",
                    "确认页面文件（虚拟内存）没有被手动关闭或设得过小，建议交给系统自动管理。",
                    "如果经常在 16GB 内存下玩现代 3A 游戏，加内存到 32GB 是最彻底的解决办法。",
                ],
                "内存压力解除后，换页导致的卡顿会消失；帧率平均值提升有限，但 1% Low（卡顿感）改善明显。",
                []));
        }

        // 提交内存（Commit）达到上限同样是内存不足的信号，但是这个指标不在 PerformanceSummary 里，
        // 只能从原始样本里取最大值——这属于实测数据，不是推测。
        var maxCommitted = MaxOf(session.Samples.Select(s => s.SystemCommittedPercent));
        if (maxCommitted.HasValue && maxCommitted.Value >= CommittedWarn)
        {
            findings.Add(New(
                "系统提交内存接近上限",
                FindingSeverity.Warning,
                "内存",
                [
                    $"提交内存（Committed）最高占用 {maxCommitted.Value:F1}%（警告线 {CommittedWarn:F0}%）",
                    $"同期可用物理内存最低 {ValueFormat.Memory(summary.SystemMinAvailableMemoryMb)}",
                    $"页面文件占用最高 {ValueFormat.Percent(summary.PageFileMaxPercent)}",
                ],
                "提交内存是「程序已申请、系统已承诺」的内存总量，包含物理内存 + 页面文件。" +
                "它接近 100% 时系统会开始拒绝新申请，游戏会出现突然卡死、贴图不加载甚至闪退。",
                [
                    "关掉后台程序释放提交内存，或用任务管理器排出占用最高的进程。",
                    "把页面文件设回「系统自动管理」，不要手动关掉。",
                    "长期不足则需要增加物理内存。",
                ],
                "无法给出确定百分比：这属于系统稳定性问题，解除压力后主要体现为不再卡死/闪退。",
                []));
        }

        var pageFile = summary.PageFileMaxPercent;
        if (pageFile.HasValue && pageFile.Value >= PageFileWarn)
        {
            findings.Add(New(
                "已在大量使用页面文件（虚拟内存）",
                FindingSeverity.Warning,
                "内存",
                [
                    $"页面文件占用最高 {pageFile.Value:F1}%（警告线 {PageFileWarn:F0}%）",
                    $"系统可用物理内存最低 {ValueFormat.Memory(summary.SystemMinAvailableMemoryMb)}",
                    session.Machine.TotalMemoryMb > 0 ? $"本机物理内存总量 {ValueFormat.Memory(session.Machine.TotalMemoryMb)}" : "物理内存总量未采集到",
                ],
                "页面文件占用高说明物理内存不够用，系统正在把内存数据往硬盘上倒腾。" +
                "读盘瞬间的卡顿、场景切换时的长时间停顿，通常就是它造成的。",
                [
                    "关闭浏览器/后台程序，释放物理内存。",
                    "降低游戏内「纹理质量」和「视距」。",
                    "确保游戏安装在 SSD 上——如果已经在用页面文件，SSD 至少比机械硬盘快一个数量级。",
                ],
                "减少换页后，卡顿感（1% Low）会明显改善；平均帧率提升有限。",
                []));
        }
    }

    // ========================================================================
    //  7. 磁盘卡顿
    // ========================================================================
    private static void CheckDisk(PerformanceSummary summary, List<Finding> findings)
    {
        var queue = summary.DiskMaxQueueLength;
        var busy = summary.DiskAvgPercent;
        var queueBad = queue.HasValue && queue.Value >= DiskQueueWarn;
        var busyBad = busy.HasValue && busy.Value >= DiskBusyWarn;
        if (!queueBad && !busyBad)
        {
            return;
        }

        var hits = new List<string>();
        if (queueBad)
        {
            hits.Add($"磁盘平均队列长度最高 {queue!.Value:F2}（警告线 {DiskQueueWarn:F0}）");
        }

        if (busyBad)
        {
            hits.Add($"磁盘总占用率平均 {busy!.Value:F1}%（警告线 {DiskBusyWarn:F0}%）");
        }

        hits.Add($"目标进程工作集 平均 {ValueFormat.Memory(summary.ProcessAvgWorkingSetMb)}");
        hits.Add($"系统可用内存最低 {ValueFormat.Memory(summary.SystemMinAvailableMemoryMb)}，页面文件占用最高 {ValueFormat.Percent(summary.PageFileMaxPercent)}");

        findings.Add(New(
            "磁盘 IO 压力大（读盘/进图会卡）",
            FindingSeverity.Warning,
            "存储",
            hits,
            "磁盘队列长度 ≥ 2 表示 IO 请求已经开始排队，程序必须等硬盘响应。" +
            "它的典型表现不是全程掉帧，而是：进图/传送/开背包时长时间卡住、贴图糊着不加载，" +
            "以及因为换页而出现的周期性顿挫。注意 \\PhysicalDisk(_Total)\\% Disk Time 在多盘机器上可能超过 100%，本报告按原值展示。",
            [
                "把游戏安装到 SSD（NVMe 优先），不要装在机械硬盘上。",
                "关闭 Windows Search 索引服务对游戏盘/游戏目录的索引。",
                "关掉 SysMain（原 Superfetch）服务：它对 SSD 收益很小，却会持续占用磁盘。",
                "检查是否有后台程序在读写大文件：Windows 更新、网盘同步、杀毒全盘扫描、录屏写盘。",
            ],
            "磁盘瓶颈解除后，「进图卡顿」这类问题会直接消失；它通常不影响平均帧率。",
            []));
    }

    // ========================================================================
    //  8. 帧率卡顿
    // ========================================================================
    private static void CheckFramePacing(PerformanceSummary summary, List<Finding> findings)
    {
        if (!summary.FpsAvg.HasValue)
        {
            return;
        }

        var lowRatio = summary.Fps1PercentLow.HasValue && summary.FpsAvg.Value > 0
            ? summary.Fps1PercentLow.Value / summary.FpsAvg.Value
            : (double?)null;
        var stuttersPerMinute = summary.StuttersPerMinute;
        var lowBad = lowRatio.HasValue && lowRatio.Value < FpsLowRatioWarn;
        var stutterBad = stuttersPerMinute.HasValue && stuttersPerMinute.Value >= StutterPerMinuteWarn;
        if (!lowBad && !stutterBad)
        {
            return;
        }

        var evidence = new List<string>
        {
            $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，最低 {ValueFormat.Fps(summary.FpsMin)}，最高 {ValueFormat.Fps(summary.FpsMax)}",
            $"1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}，0.1% Low {ValueFormat.Fps(summary.Fps01PercentLow)}",
            lowRatio.HasValue
                ? $"1% Low / 平均帧率 = {lowRatio.Value:F2}（低于 {FpsLowRatioWarn:F2} 视为帧时间不稳定）"
                : "1% Low 未采集到，无法计算稳定性比值",
            $"帧生成时间 P99 {ValueFormat.Ms(summary.FrameTimeP99Ms)}，标准差 {ValueFormat.Ms(summary.FrameTimeStdDevMs)}",
            $"判定为卡顿的帧（帧时间 > 2×中位数）共 {summary.StutterCount} 次" +
                (stuttersPerMinute.HasValue ? $"，折合 {stuttersPerMinute.Value:F1} 次/分钟（警告线 {StutterPerMinuteWarn:F0}）" : "（采样时长不足，无法折算每分钟次数）"),
            $"瓶颈判定：{summary.BottleneckVerdict}",
        };

        var recommendations = new List<string>();
        switch (summary.BottleneckVerdict)
        {
            case "CPU":
                recommendations.Add("CPU 侧：降「视距/绘制距离」「粒子数量」「AI/单位数量」，关闭后台程序。");
                break;
            case "GPU":
                recommendations.Add("GPU 侧：降阴影/体积雾/光追，开 DLSS/FSR，或把帧率上限设到刷新率以下。");
                break;
            default:
                recommendations.Add("先按占用率判断方向：CPU 高就降 CPU 相关项，GPU 高就降 GPU 相关项，一次只改一项再测。");
                break;
        }

        recommendations.Add("内存侧：如果可用内存低于 4GB 或页面文件占用高，先解决内存/换页问题，卡顿往往随之消失。");
        recommendations.Add("着色器编译：新装游戏/更新驱动后的前几十分钟卡顿多为着色器编译，先在场景里跑几分钟等它编译完再测。");
        recommendations.Add("限制帧率到「刷新率 − 3」或开启垂直同步，用轻微的延迟代价换帧时间稳定。");
        recommendations.Add("检查是否开着录屏/直播/叠加层（Discord、GeForce Experience、Xbox Game Bar），它们会造成周期性卡顿。");

        findings.Add(New(
            "帧时间不稳定（卡顿）",
            FindingSeverity.Warning,
            "卡顿",
            evidence,
            "平均帧率看着还行，但 1% Low 远低于平均值，说明帧与帧之间的耗时忽长忽短。" +
            "玩家感受到的不是「帧率低」而是「一卡一卡」。常见原因按概率排序：CPU 主线程被占用、" +
            "内存不足导致换页、着色器编译、后台程序抢资源、垂直同步与帧率不匹配。",
            recommendations,
            "卡顿问题的改善体现在 1% Low 和卡顿次数上，而不是平均帧率；请用本工具重测后对比这两项。",
            []));
    }

    // ========================================================================
    //  9. 画质与硬件是否匹配
    // ========================================================================
    private static void CheckGraphicsMatch(DiagnosisReport report, PerformanceSummary summary, List<Finding> findings)
    {
        var insights = report.GraphicsInsights;
        if (insights.Count == 0)
        {
            findings.Add(New(
                "没有找到可解读的画质配置文件",
                FindingSeverity.Info,
                "画质设置",
                [
                    "本次没有在常见位置发现该游戏的画质配置文件",
                    $"目标进程：{report.Session.ProcessName}（{report.Session.ProcessPath}）",
                ],
                "画质解读依赖游戏写在磁盘上的配置文件（ini/json/xml）。有些游戏把设置存在云端或注册表里，" +
                "有些用加密/二进制格式，这些情况读不到原始键值。本工具不会去猜你的画质设置。",
                [
                    "进入游戏 →「图形/画面设置」，手动对照本报告的硬件结论调整（优先降阴影、体积雾、光追）。",
                    "如果知道该游戏的配置文件路径，可以手动确认它是否在常见位置之外。",
                ],
                "无法估计：没有配置数据时不给出画质建议的收益。",
                []));
            return;
        }

        var gpu = summary.GpuAvgUtilPercent;
        var fpsLow = summary.FpsAvg.HasValue && summary.FpsAvg.Value < FpsSmoothReference;

        // 9.1 光追
        var rayTracing = insights.FirstOrDefault(i =>
            i.PerformanceImpact >= 5 &&
            (i.SettingName.Contains("光追", StringComparison.Ordinal) ||
             i.SourceKey.Contains("RayTracing", StringComparison.OrdinalIgnoreCase) ||
             i.SourceKey.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
             i.SourceKey.Contains("Lumen", StringComparison.OrdinalIgnoreCase)));
        if (rayTracing is not null && !IsOff(rayTracing.NormalizedValue)
            && ((gpu.HasValue && gpu.Value >= GpuAlmostMaxed) || fpsLow))
        {
            findings.Add(New(
                "光追开着，而显卡已经接近满载",
                FindingSeverity.Warning,
                "画质设置",
                [
                    $"画质项 {rayTracing.SettingName}：当前 {rayTracing.NormalizedValue}（原始值 {rayTracing.RawValue}）",
                    $"性能影响评级 {rayTracing.PerformanceImpact}/5（最高档）",
                    $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}（接近满载线 {GpuAlmostMaxed:F0}%）",
                    summary.FpsAvg.HasValue ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}" : "本次没有采集到帧率数据",
                ],
                "光追（Ray Tracing）会额外做光线求交计算，是当前最吃显卡的一类特效，" +
                "在移动端显卡上开销尤其大。它开着的时候，显卡占用会长时间贴着 100%，帧率被压到底。",
                [
                    "把光追从「超高/高」降到「中」或直接关闭，通常是最省事、收益最大的一步。",
                    "如果不想关光追，就开 DLSS/FSR 并把档位调到「性能」，用超分补回帧率。",
                    "同时把「光追反射」「光追阴影」「光追全局光照」这些子项分开降，而不是一次全关。",
                ],
                "关闭光追能显著降低 GPU 负载，但具体帧率提升幅度取决于该游戏的光追实现，本工具不做百分比承诺。",
                [$"{rayTracing.SourceKey} = {rayTracing.RawValue}（{rayTracing.SettingName}）"]));
        }

        // 9.2 画质总开销
        var impactSum = insights.Sum(i => i.PerformanceImpact);
        if (impactSum >= GraphicsImpactSumHigh && gpu.HasValue && gpu.Value >= GpuSaturated)
        {
            var top = insights.OrderByDescending(i => i.PerformanceImpact).Take(5)
                .Select(i => $"{i.SettingName}({i.NormalizedValue}, 影响 {i.PerformanceImpact}/5)");
            findings.Add(New(
                "画质整体档位偏高，超出当前显卡的余量",
                FindingSeverity.Warning,
                "画质设置",
                [
                    $"已识别的画质项性能影响合计 {impactSum} 分（参考线 {GraphicsImpactSumHigh:F0} 分，共 {insights.Count} 项）",
                    $"最吃性能的几项：{string.Join("；", top)}",
                    $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}（满载线 {GpuSaturated:F0}%）",
                    summary.FpsAvg.HasValue ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}" : "本次没有采集到帧率数据",
                ],
                "画质项的「性能影响」是按该项的常见开销给出的经验估值（不是实测），" +
                "合计分数高说明同时开了很多吃性能的特效，显卡没有余量应付突发负载，帧率会持续偏低。",
                [
                    "用游戏自带的「画质预设」整体降一档（例如从「高」降到「中」），再单独把最在意的项调回来。",
                    "优先降：光追 → 体积雾/体积云 → 阴影 → 抗锯齿 → 后期处理，这几项性价比最高。",
                    "开启 DLSS/FSR/TSR 的质量档，多数情况下比直接降画质更划算。",
                ],
                "整体降一档后 GPU 占用会明显下降，帧率提升幅度取决于降了哪些项；本工具不做百分比承诺。",
                top.ToList()));
        }

        // 9.3 垂直同步
        var vsync = insights.FirstOrDefault(i =>
            i.SettingName.Contains("垂直同步", StringComparison.Ordinal) ||
            i.SourceKey.Contains("VSync", StringComparison.OrdinalIgnoreCase) ||
            i.SourceKey.Contains("bVSync", StringComparison.OrdinalIgnoreCase));
        if (vsync is not null && !IsOff(vsync.NormalizedValue)
            && summary.FpsAvg.HasValue && summary.FpsAvg.Value > FpsSmoothReference)
        {
            findings.Add(New(
                "垂直同步已开启，而帧率能超过 60",
                FindingSeverity.Info,
                "画质设置",
                [
                    $"画质项 {vsync.SettingName}：当前 {vsync.NormalizedValue}（原始值 {vsync.RawValue}）",
                    $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}",
                ],
                "垂直同步把渲染节奏锁在显示器刷新率上，能消除画面撕裂，但会引入 1~2 帧的额外输入延迟。" +
                "如果帧率本来就能稳定超过刷新率，用驱动级限帧（NVIDIA 控制面板的「最大帧速率」）" +
                "能同时做到不撕裂、延迟更低。",
                [
                    "关闭游戏内垂直同步，改为在 NVIDIA 控制面板为这个游戏设置「最大帧速率 = 刷新率 − 3」。",
                    "同时开启 G-Sync/FreeSync（如果显示器支持），这是延迟最低的防撕裂方案。",
                    "如果游戏里出现明显撕裂且显示器不支持可变刷新率，垂直同步可以保留。",
                ],
                "这项改善的是输入延迟和手感，不是帧率；帧率上限保持不变。",
                [$"{vsync.SourceKey} = {vsync.RawValue}（{vsync.SettingName}）"]));
        }

        // 9.4 高分辨率 + 显卡满载
        var resolution = insights.FirstOrDefault(i => i.SettingName.Contains("分辨率", StringComparison.Ordinal));
        if (resolution is not null && gpu.HasValue && gpu.Value >= GpuAlmostMaxed)
        {
            var match = ResolutionPattern.Match(resolution.NormalizedValue + " " + resolution.RawValue);
            if (match.Success
                && int.TryParse(match.Groups["w"].Value, out var width)
                && int.TryParse(match.Groups["h"].Value, out var height)
                && (width > 1920 || height > 1080))
            {
                findings.Add(New(
                    "分辨率高于 1080p，显卡已被拉满",
                    FindingSeverity.Warning,
                    "画质设置",
                    [
                        $"画质项 {resolution.SettingName}：当前 {resolution.NormalizedValue}（原始值 {resolution.RawValue}）",
                        $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}（接近满载线 {GpuAlmostMaxed:F0}%）",
                        summary.FpsAvg.HasValue ? $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}" : "本次没有采集到帧率数据",
                    ],
                    $"像素数量决定了显卡的基础工作量：{width}×{height} 的像素量约为 1920×1080 的 " +
                    $"{(double)width * height / (1920 * 1080):F2} 倍，显卡占用自然更高。",
                    [
                        "开启 DLSS / FSR「质量」档，让显卡按 1080p 级别渲染再放大到当前分辨率，画质损失很小。",
                        "如果显示器支持，切换到 1920×1080 全屏再测一次，用两份报告的帧率差确认分辨率的实际代价。",
                        "降低「分辨率缩放 / 渲染比例」到 85%~90%。",
                    ],
                    "分辨率或超分档位调整后 GPU 负载会显著变化，帧率提升幅度与引擎实现相关，本工具不做百分比承诺。",
                    [$"{resolution.SourceKey} = {resolution.RawValue}（{resolution.SettingName}）"]));
            }
        }
    }

    // ========================================================================
    //  10. 后台干扰
    // ========================================================================
    private static void CheckBackgroundLoad(SampleSession session, List<Finding> findings)
    {
        var logicalCount = session.Machine.CpuLogicalProcessors > 0
            ? session.Machine.CpuLogicalProcessors
            : 0;

        var compared = 0;
        var hits = 0;
        double gapSum = 0;
        double systemSum = 0;
        double processSum = 0;

        foreach (var sample in session.Samples)
        {
            if (!sample.CpuTotalPercent.HasValue || !sample.ProcessCpuPercent.HasValue)
            {
                continue;
            }

            var cores = sample.CpuLogicalCount ?? logicalCount;
            // ProcessCpuPercent 是「相对单核」的占用（可能 >100），
            // 要除以逻辑处理器数量才能和整机 CPU 总占用做同类比较。
            var processSystemWide = cores > 0 ? sample.ProcessCpuPercent.Value / cores : sample.ProcessCpuPercent.Value;
            var gap = sample.CpuTotalPercent.Value - processSystemWide;

            compared++;
            systemSum += sample.CpuTotalPercent.Value;
            processSum += processSystemWide;
            if (gap >= BackgroundGapPercent)
            {
                hits++;
                gapSum += gap;
            }
        }

        if (compared == 0)
        {
            return;
        }

        var ratio = (double)hits / compared;
        if (ratio < BackgroundSampleRatio)
        {
            return;
        }

        var avgGap = hits > 0 ? gapSum / hits : 0;

        findings.Add(New(
            "存在后台进程干扰",
            avgGap >= 40 ? FindingSeverity.Warning : FindingSeverity.Info,
            "后台干扰",
            [
                $"有 {hits}/{compared} 个样本（{ratio * 100:F1}%）里，系统 CPU 总占用比目标进程占用高出 {BackgroundGapPercent:F0} 个百分点以上（判定线 {(BackgroundSampleRatio * 100):F0}% 的样本）",
                $"这些样本的平均差值 {avgGap:F1} 个百分点",
                $"全程平均：系统 CPU {systemSum / compared:F1}%，目标进程（折算成整机百分比）{processSum / compared:F1}%",
                logicalCount > 0
                    ? $"目标进程原始占用是「相对单核」的百分比，已按 {logicalCount} 个逻辑处理器折算成整机百分比"
                    : "逻辑处理器数量未采集到，未做折算，差值为原始值对比",
                $"目标进程：{session.ProcessName}（PID {session.ProcessId}）",
            ],
            "系统 CPU 占用明显高于游戏进程本身，说明有别的程序在持续吃 CPU。" +
            "这些程序即使只占 20%~30%，也会抢走游戏主线程需要的核心时间，典型表现是帧时间忽长忽短、1% Low 变差。",
            [
                "关掉浏览器（视频、直播页面尤其吃 CPU）、聊天软件、录屏/直播工具。",
                "关掉 RGB 灯控、主板/显卡厂商的超频监控软件（Armoury Crate、iCUE、MSI Center 等）。",
                "把杀毒软件的实时扫描改为「游戏时暂停」，或把游戏目录加入排除项。",
                "检查 Windows 更新是否在后台下载/安装，以及是否有网盘同步在跑。",
            ],
            "清掉后台占用后，CPU 会空出核心给游戏主线程，卡顿感（1% Low）通常立刻改善。",
            []));
    }

    // ========================================================================
    //  11. 能力说明
    // ========================================================================
    private static void CheckCapabilities(SampleSession session, List<Finding> findings)
    {
        var unavailable = session.Machine.Capabilities.Where(c => !c.Available).ToList();
        if (unavailable.Count == 0)
        {
            return;
        }

        var evidence = new List<string>();
        foreach (var capability in unavailable)
        {
            evidence.Add(string.IsNullOrWhiteSpace(capability.Detail)
                ? $"【不可用】{capability.Name}"
                : $"【不可用】{capability.Name}：{capability.Detail}");
        }

        foreach (var warning in session.Warnings)
        {
            evidence.Add($"【采集告警】{warning}");
        }

        var recommendations = new List<string>();
        if (unavailable.Any(c => c.Name.Contains("帧率", StringComparison.Ordinal) || c.Name.Contains("FPS", StringComparison.OrdinalIgnoreCase)))
        {
            recommendations.Add("帧率相关能力不可用：本工具需要以管理员身份运行才能做逐帧采集，请右键 →「以管理员身份运行」后重测。");
        }

        if (unavailable.Any(c => c.Name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("NVML", StringComparison.OrdinalIgnoreCase)))
        {
            recommendations.Add("nvidia-smi/NVML 不可用：安装或更新 NVIDIA 官方驱动（GeForce 驱动自带 nvidia-smi）后重测，即可拿到 GPU 频率/温度/功耗。");
        }

        if (!session.Machine.IsElevated)
        {
            recommendations.Add("当前未以管理员身份运行：部分性能计数器与逐帧采集会受限。");
        }

        recommendations.Add("报告里凡是显示「本机采集不到」的字段，都是上述能力缺失导致的，本工具不会用估算值填充。");

        findings.Add(New(
            "采集能力说明（报告里为什么缺数据）",
            FindingSeverity.Info,
            "数据可信度",
            evidence,
            "本工具只报告真实采集到的数据。以下采集能力在当前环境下不可用，因此报告里对应的指标为空，" +
            "相关的诊断结论也无法给出——这不是数据丢失，而是本机/本权限下采不到。",
            recommendations,
            "补齐采集能力后重测，报告会覆盖更多指标（尤其是帧率与 GPU 频率/温度）。",
            []));
    }

    // ========================================================================
    //  附加：没有帧率数据时要说明清楚
    // ========================================================================
    private static void CheckMissingFpsData(SampleSession session, PerformanceSummary summary, List<Finding> findings)
    {
        if (summary.FpsAvg.HasValue)
        {
            return;
        }

        var fpsCapability = session.Machine.Capabilities.FirstOrDefault(c =>
            c.Name.Contains("帧率", StringComparison.Ordinal) || c.Name.Contains("FPS", StringComparison.OrdinalIgnoreCase));

        findings.Add(New(
            "没有帧率数据，本次无法判断流畅度",
            FindingSeverity.Info,
            "数据可信度",
            [
                $"有效帧率样本数 {session.FpsSampleCount}（计算帧率指标需要至少 {SummaryCalculator.MinimumFpsSamples} 个）",
                $"帧率来源：{(string.IsNullOrWhiteSpace(session.Samples.FirstOrDefault(s => s.FpsSource is not null)?.FpsSource) ? "无" : session.Samples.First(s => s.FpsSource is not null).FpsSource)}",
                fpsCapability is null
                    ? "本机能力探测里没有帧率相关条目"
                    : $"帧率采集能力：{(fpsCapability.Available ? "可用" : "不可用")}——{fpsCapability.Detail}",
            ],
            "帧率是最直观的流畅度指标。采不到帧率时，报告里的 CPU/GPU/内存结论依然成立（它们来自实测占用率），" +
            "但无法判断「到底卡不卡」「掉帧掉多少」。",
            [
                "以管理员身份重新运行本工具后重测（逐帧采集帧率需要管理员权限）。",
                "如果游戏使用反作弊，帧率采集可能被拦截，此时请以游戏内自带的帧率显示为准。",
            ],
            "补齐帧率采集后，报告会额外给出平均帧率、1% Low、卡顿次数等指标。",
            []));
    }

    // ========================================================================
    //  12. 正常项
    // ========================================================================
    private static void CheckHealthySigns(PerformanceSummary summary, List<Finding> findings)
    {
        if (summary.GpuMaxTempC.HasValue && summary.GpuMaxTempC.Value < GpuTempWarn)
        {
            findings.Add(New(
                "GPU 温度健康",
                FindingSeverity.Good,
                "功耗与散热",
                [
                    $"GPU 最高温度 {ValueFormat.Temp(summary.GpuMaxTempC)}，平均 {ValueFormat.Temp(summary.GpuAvgTempC)}（都低于 {GpuTempWarn:F0}℃ 的降频参考线）",
                    $"GPU 平均功耗 {ValueFormat.Watt(summary.GpuAvgPowerW)}，平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}",
                ],
                "显卡温度没有进入降频区间，可以认为它跑在当前散热条件下的正常水平，不会因为过热而损失性能。",
                ["保持机箱/笔记本进风口通畅即可，无需额外处理。"],
                "无需优化。",
                []));
        }

        // 只有温度被验证为"跟着负载走"时才敢说 CPU 温度正常；否则上面已经发过一条能力说明。
        if (summary.CpuTempTracksLoad == true
            && summary.CpuMaxTempC.HasValue && summary.CpuMaxTempC.Value < CpuTempWarn)
        {
            findings.Add(New(
                "CPU 温度正常",
                FindingSeverity.Good,
                "功耗与散热",
                [
                    $"CPU 封装最高温度 {ValueFormat.Temp(summary.CpuMaxTempC)}，平均 {ValueFormat.Temp(summary.CpuAvgTempC)}（低于 {CpuTempWarn:F0}℃ 的警告线）",
                ],
                "CPU 没有撞到温度保护线，睿频不会因为过热被削减。",
                ["无需处理。"],
                "无需优化。",
                []));
        }

        if (summary.SystemMinAvailableMemoryMb.HasValue && summary.SystemMinAvailableMemoryMb.Value >= MemoryComfortableMb)
        {
            findings.Add(New(
                "系统内存充裕",
                FindingSeverity.Good,
                "内存",
                [
                    $"采样期间可用物理内存最低 {ValueFormat.Memory(summary.SystemMinAvailableMemoryMb)}（充裕线 {MemoryComfortableMb:F0} MB）",
                    $"页面文件占用最高 {ValueFormat.Percent(summary.PageFileMaxPercent)}",
                ],
                "可用内存始终充足，采样期间没有发生因内存不足导致的换页，不会出现由此引发的卡顿。",
                ["无需处理。"],
                "无需优化。",
                []));
        }

        if (summary.FpsAvg.HasValue && summary.StutterCount == 0)
        {
            findings.Add(New(
                "帧时间稳定，没有检测到卡顿帧",
                FindingSeverity.Good,
                "卡顿",
                [
                    $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}，0.1% Low {ValueFormat.Fps(summary.Fps01PercentLow)}",
                    $"帧生成时间 P99 {ValueFormat.Ms(summary.FrameTimeP99Ms)}，标准差 {ValueFormat.Ms(summary.FrameTimeStdDevMs)}",
                    $"判定为卡顿的帧（帧时间 > 2×中位数）共 {summary.StutterCount} 次",
                ],
                "帧生成时间分布集中，没有出现明显离群的卡顿帧，说明当前场景下 CPU/内存/IO 都没有造成周期性阻塞。",
                ["无需处理；建议在不同场景（战斗、大地图）再各测一次做对比。"],
                "无需优化。",
                []));
        }

        if (summary.FpsAvg.HasValue && summary.FpsAvg.Value >= FpsSmoothReference
            && (!summary.Fps1PercentLow.HasValue || summary.Fps1PercentLow.Value / summary.FpsAvg.Value >= FpsLowRatioWarn))
        {
            findings.Add(New(
                "帧率达标且稳定",
                FindingSeverity.Good,
                "性能瓶颈",
                [
                    $"平均帧率 {ValueFormat.Fps(summary.FpsAvg)}，最低 {ValueFormat.Fps(summary.FpsMin)}，1% Low {ValueFormat.Fps(summary.Fps1PercentLow)}",
                    $"GPU 平均占用 {ValueFormat.Percent(summary.GpuAvgUtilPercent)}，CPU 平均占用 {ValueFormat.Percent(summary.CpuAvgPercent)}",
                    $"瓶颈判定：{summary.BottleneckVerdict}",
                ],
                $"平均帧率达到 {FpsSmoothReference:F0} FPS 参考线，且 1% Low 与平均值之比在 {FpsLowRatioWarn:F2} 以上，说明帧率不仅高而且稳。",
                ["无需处理。若想进一步降低延迟，可考虑用驱动级限帧替代游戏内垂直同步。"],
                "无需优化。",
                []));
        }
    }

    // ========================================================================
    //  工具方法
    // ========================================================================

    /// <summary>
    /// 严重程度排序权重。注意：不能直接用 <see cref="FindingSeverity"/> 的枚举数值排序——
    /// 契约里 Good(1) 大于 Info(0)，但报告的期望顺序是 Critical &gt; Warning &gt; Minor &gt; Info &gt; Good。
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

    private static List<Finding> SortFindings(List<Finding> findings) =>
        findings.OrderByDescending(f => SeverityRank(f.Severity))
                .ThenBy(f => f.Category, StringComparer.Ordinal)
                .ToList();

    private static Finding New(
        string title,
        FindingSeverity severity,
        string category,
        List<string> evidence,
        string explanation,
        List<string> recommendations,
        string expectedGain,
        List<string> configEvidence) => new()
        {
            Title = title,
            Severity = severity,
            Category = category,
            Evidence = evidence,
            Explanation = explanation,
            Recommendations = recommendations,
            ExpectedGain = expectedGain,
            ConfigEvidence = configEvidence,
        };

    private static bool IsOff(string normalized) =>
        normalized.Contains("关闭", StringComparison.Ordinal) ||
        normalized.Contains("关", StringComparison.Ordinal) ||
        string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(normalized, "0", StringComparison.Ordinal);

    private static double? MaxOf(IEnumerable<double?> values)
    {
        double? result = null;
        foreach (var value in values)
        {
            if (!value.HasValue || double.IsNaN(value.Value))
            {
                continue;
            }

            if (!result.HasValue || value.Value > result.Value)
            {
                result = value;
            }
        }

        return result;
    }
}
