namespace Gpd.Core.Analysis;

/// <summary>
/// 把一次采集的原始样本（<see cref="SampleSession"/>）折算成报告用的统计摘要（<see cref="PerformanceSummary"/>）。
/// <para>
/// 【诚实原则】只统计真实采集到的值：某个指标全程为 <c>null</c> 时，结果字段也保持 <c>null</c>，
/// 绝不用 0 冒充实测数据。报告层会把 <c>null</c> 渲染成"本机采集不到"。
/// </para>
/// <para>
/// 1% Low / 0.1% Low 采用业界标准做法：把窗口内的帧生成时间升序排序，
/// 取第 99% / 99.9% 分位（nearest-rank）的帧时间，再换算回 FPS（<c>1000 / 帧时间</c>）。
/// </para>
/// </summary>
public static class SummaryCalculator
{
    /// <summary>帧率指标的最低样本数门槛：少于这个数就不计算帧率相关指标（留 null，不编造）。</summary>
    public const int MinimumFpsSamples = 10;

    /// <summary>计算整段采样的统计摘要。</summary>
    public static PerformanceSummary Calculate(SampleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var samples = session.Samples;
        var summary = new PerformanceSummary
        {
            SampleCount = samples.Count,
            DurationSeconds = ResolveDurationSeconds(session),
        };

        // ---- CPU：逐项只在非 null 的样本上求平均/最大 ----
        summary.CpuAvgPercent = Average(samples.Select(s => s.CpuTotalPercent));
        summary.CpuMaxPercent = Max(samples.Select(s => s.CpuTotalPercent));
        summary.CpuAvgFrequencyMhz = Average(samples.Select(s => s.CpuFrequencyMhz));
        summary.CpuMaxFrequencyMhz = Max(samples.Select(s => s.CpuFrequencyMhz));
        summary.CpuAvgTempC = Average(samples.Select(s => s.CpuPackageTempC));
        summary.CpuMaxTempC = Max(samples.Select(s => s.CpuPackageTempC));
        summary.CpuAvgPowerW = Average(samples.Select(s => s.CpuPackagePowerW));
        summary.CpuMaxPowerW = Max(samples.Select(s => s.CpuPackagePowerW));

        // ---- GPU ----
        summary.GpuAvgUtilPercent = Average(samples.Select(s => s.GpuUtilPercent));
        summary.GpuMaxUtilPercent = Max(samples.Select(s => s.GpuUtilPercent));
        summary.GpuAvgClockMhz = Average(samples.Select(s => s.GpuClockMhz));
        summary.GpuMaxClockMhz = Max(samples.Select(s => s.GpuClockMhz));
        summary.GpuAvgTempC = Average(samples.Select(s => s.GpuTempC));
        summary.GpuMaxTempC = Max(samples.Select(s => s.GpuTempC));
        summary.GpuAvgPowerW = Average(samples.Select(s => s.GpuPowerW));
        summary.GpuMaxPowerW = Max(samples.Select(s => s.GpuPowerW));
        summary.GpuPowerLimitW = Average(samples.Select(s => s.GpuPowerLimitW));

        // 功耗受限占比：只有"实际功耗"和"功耗上限"同时存在的样本才计入分母，
        // 否则算出来的占比会被缺数据的样本稀释，属于编造。
        var powerSamples = samples.Where(s => s.GpuPowerW.HasValue && s.GpuPowerLimitW.HasValue).ToList();
        var powerLimited = powerSamples.Count(s => s.GpuPowerLimitW!.Value > 0
                                                  && s.GpuPowerW!.Value >= 0.95 * s.GpuPowerLimitW!.Value);
        summary.GpuPowerLimitedPercent = Percent(powerLimited, powerSamples.Count);

        // 温度风险占比：分母是所有采到 GPU 温度的样本。
        var tempSamples = samples.Where(s => s.GpuTempC.HasValue).ToList();
        summary.GpuThermalRiskPercent = Percent(tempSamples.Count(s => s.GpuTempC!.Value >= 83), tempSamples.Count);

        // ---- 内存 / 存储 ----
        summary.ProcessAvgWorkingSetMb = Average(samples.Select(s => s.ProcessWorkingSetMb));
        summary.ProcessMaxWorkingSetMb = Max(samples.Select(s => s.ProcessWorkingSetMb));
        summary.SystemMinAvailableMemoryMb = Min(samples.Select(s => s.SystemAvailableMemoryMb));
        summary.PageFileMaxPercent = Max(samples.Select(s => s.PageFilePercent));
        summary.DiskAvgPercent = Average(samples.Select(s => s.DiskPercent));
        // 磁盘 100% 尖峰是卡顿的常见直接原因，平均值会把它抹平，所以峰值要单独统计
        summary.DiskMaxPercent = Max(samples.Select(s => s.DiskPercent));
        summary.DiskMaxQueueLength = Max(samples.Select(s => s.DiskQueueLength));

        // CPU 与 GPU 占用的平均差值（取绝对值），用于判断谁先吃满。
        var pairs = samples.Where(s => s.CpuTotalPercent.HasValue && s.GpuUtilPercent.HasValue)
                           .Select(s => Math.Abs(s.CpuTotalPercent!.Value - s.GpuUtilPercent!.Value))
                           .ToList();
        summary.CpuGpuGapPercent = pairs.Count == 0 ? null : pairs.Average();

        // ---- 帧率（样本不足时全部留 null）----
        FillFpsMetrics(summary, session);

        summary.BottleneckVerdict = JudgeBottleneck(summary);
        summary.CpuTempTracksLoad = JudgeCpuTempTracksLoad(samples);
        return summary;
    }

    /// <summary>判定温度读数是否真的跟着 CPU 负载走的分组门槛。</summary>
    private const double IdleCpuPercentThreshold = 25.0;
    private const double LoadCpuPercentThreshold = 60.0;
    private const int MinSamplesPerGroup = 3;
    /// <summary>高负载组比低负载组至少要高出这么多度，才算"温度跟着负载走"。</summary>
    private const double TempRiseThreshold = 3.0;

    /// <summary>
    /// 判断 CPU 温度读数是否随负载上升。存在的理由：ACPI 热区不一定是 CPU 封装温度。
    /// 实测本机 ACPI 热区 <c>\_TZ.TZ0</c> 空闲 85.1℃、16 线程满载 82.7℃，满载反而更低——
    /// 真 CPU 封装温度不可能这样，所以它只是个主板/机壳热区。若不区分，报告会给出
    /// "CPU 温度正常/过高"这种建立在错误传感器上的建议。
    /// 负载跨度不足时返回 null（样本不够就不下结论，不猜）。
    /// </summary>
    private static bool? JudgeCpuTempTracksLoad(List<PerformanceSample> samples)
    {
        var usable = samples
            .Where(s => s.CpuTotalPercent.HasValue && s.CpuPackageTempC.HasValue)
            .Select(s => (Cpu: s.CpuTotalPercent!.Value, Temp: s.CpuPackageTempC!.Value))
            .ToList();

        var idle = usable.Where(x => x.Cpu <= IdleCpuPercentThreshold).Select(x => x.Temp).ToList();
        var load = usable.Where(x => x.Cpu >= LoadCpuPercentThreshold).Select(x => x.Temp).ToList();

        if (idle.Count < MinSamplesPerGroup || load.Count < MinSamplesPerGroup) return null;

        return load.Average() - idle.Average() >= TempRiseThreshold;
    }

    /// <summary>
    /// 帧率相关指标。只有 <see cref="SampleSession.FpsSampleCount"/> 达到 <see cref="MinimumFpsSamples"/>
    /// 时才计算；否则一律保持 null，由报告层明确写出"没有采集到帧率数据"。
    /// </summary>
    private static void FillFpsMetrics(PerformanceSummary summary, SampleSession session)
    {
        if (session.FpsSampleCount < MinimumFpsSamples)
        {
            summary.StutterCount = 0;
            summary.StuttersPerMinute = null;
            return;
        }

        var fpsValues = session.Samples
            .Where(s => s.Fps.HasValue && s.Fps!.Value > 0)
            .Select(s => s.Fps!.Value)
            .ToList();

        if (fpsValues.Count == 0)
        {
            summary.StutterCount = 0;
            return;
        }

        summary.FpsAvg = fpsValues.Average();
        summary.FpsMin = fpsValues.Min();
        summary.FpsMax = fpsValues.Max();

        // 帧生成时间：优先用采集器直接给出的 FrameTimeMs，缺失时按 1000/FPS 反推。
        var frameTimes = session.Samples
            .Where(s => s.Fps.HasValue && s.Fps!.Value > 0)
            .Select(s => s.FrameTimeMs.HasValue && s.FrameTimeMs!.Value > 0 ? s.FrameTimeMs!.Value : 1000.0 / s.Fps!.Value)
            .Where(t => t > 0 && !double.IsNaN(t) && !double.IsInfinity(t))
            .OrderBy(t => t)
            .ToList();

        if (frameTimes.Count == 0)
        {
            return;
        }

        var p99 = PercentileNearestRank(frameTimes, 0.99);
        var p999 = PercentileNearestRank(frameTimes, 0.999);
        var median = PercentileNearestRank(frameTimes, 0.5);

        summary.FrameTimeP99Ms = p99;
        summary.Fps1PercentLow = p99 > 0 ? 1000.0 / p99 : null;
        summary.Fps01PercentLow = p999 > 0 ? 1000.0 / p999 : null;

        // 帧时间标准差（总体标准差，除以 n）：抖动指标，越大越不稳。
        var mean = frameTimes.Average();
        var variance = frameTimes.Sum(t => (t - mean) * (t - mean)) / frameTimes.Count;
        summary.FrameTimeStdDevMs = Math.Sqrt(variance);

        // 卡顿帧定义：帧生成时间 > 2 × 中位数。
        var stutters = frameTimes.Count(t => t > 2.0 * median);
        summary.StutterCount = stutters;
        var duration = summary.DurationSeconds;
        summary.StuttersPerMinute = duration > 0 ? stutters * 60.0 / duration : null;
    }

    /// <summary>
    /// 瓶颈判定。全部依据实测平均值，不做无数据的推断：
    /// <list type="number">
    /// <item>CPU 平均 ≥ 80% 且 GPU 平均 &lt; 80% → "CPU"（CPU 先吃满，GPU 还有余量）。</item>
    /// <item>GPU 平均 ≥ 90% 且 CPU 平均 &lt; 70% → "GPU"（GPU 接近满载，CPU 有余量）。</item>
    /// <item>两者都高（GPU ≥ 90 且 CPU ≥ 70，或 CPU ≥ 80 且 GPU ≥ 80）→ "均衡"（两边都吃紧，不能只怪一方）。</item>
    /// <item>没有帧率数据 且 GPU 占用 &lt; 40%（或压根采不到 GPU 占用）→ "未知"（数据不足以判断）。</item>
    /// <item>其余 → "均衡"。</item>
    /// </list>
    /// </summary>
    private static string JudgeBottleneck(PerformanceSummary summary)
    {
        var cpu = summary.CpuAvgPercent;
        var gpu = summary.GpuAvgUtilPercent;

        if (cpu.HasValue && cpu.Value >= 80 && gpu.HasValue && gpu.Value < 80)
        {
            return "CPU";
        }

        if (gpu.HasValue && gpu.Value >= 90 && (!cpu.HasValue || cpu.Value < 70))
        {
            return "GPU";
        }

        if (gpu.HasValue && gpu.Value >= 90 && cpu.HasValue && cpu.Value >= 70)
        {
            return "均衡";
        }

        if (cpu.HasValue && cpu.Value >= 80 && gpu.HasValue && gpu.Value >= 80)
        {
            return "均衡";
        }

        if (!summary.FpsAvg.HasValue && (!gpu.HasValue || gpu.Value < 40))
        {
            // 必须区分两种情况，否则报告会自相矛盾：上面明明列出了"GPU 平均占用 36%"，
            // 结论却写"采集不到 GPU 占用"。采到了但偏低，和压根没采到，是两件事。
            return gpu.HasValue
                ? $"未知（GPU 占用仅 {gpu.Value:F1}%，且没有帧率数据，不足以判断瓶颈）"
                : "未知（采集不到 GPU 占用）";
        }

        return "均衡";
    }

    /// <summary>采样时长：优先用起止时间，取不到时退化成首尾样本的相对秒差。</summary>
    private static double ResolveDurationSeconds(SampleSession session)
    {
        // EndedAt 在契约里是非空 DateTimeOffset，未赋值时等于 default，必须当成"没数据"
        if (session.StartedAt != default && session.EndedAt != default)
        {
            var wallClock = (session.EndedAt - session.StartedAt).TotalSeconds;
            if (wallClock > 0)
            {
                return wallClock;
            }
        }

        if (session.Samples.Count >= 2)
        {
            var span = session.Samples[^1].ElapsedSeconds - session.Samples[0].ElapsedSeconds;
            if (span > 0)
            {
                return span;
            }
        }

        return 0;
    }

    private static double? Average(IEnumerable<double?> values)
    {
        double sum = 0;
        var count = 0;
        foreach (var value in values)
        {
            if (!IsUsable(value))
            {
                continue;
            }

            sum += value!.Value;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    private static double? Max(IEnumerable<double?> values)
    {
        double? result = null;
        foreach (var value in values)
        {
            if (!IsUsable(value))
            {
                continue;
            }

            if (!result.HasValue || value!.Value > result.Value)
            {
                result = value;
            }
        }

        return result;
    }

    private static double? Min(IEnumerable<double?> values)
    {
        double? result = null;
        foreach (var value in values)
        {
            if (!IsUsable(value))
            {
                continue;
            }

            if (!result.HasValue || value!.Value < result.Value)
            {
                result = value;
            }
        }

        return result;
    }

    private static double? Percent(int hits, int total) => total <= 0 ? null : hits * 100.0 / total;

    private static bool IsUsable(double? value) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);

    /// <summary>nearest-rank 分位数：入参必须已升序排序。</summary>
    private static double PercentileNearestRank(IReadOnlyList<double> sortedAscending, double percentile)
    {
        var n = sortedAscending.Count;
        if (n == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * n);
        rank = Math.Clamp(rank, 1, n);
        return sortedAscending[rank - 1];
    }
}
