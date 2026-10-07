using System.Globalization;
using System.IO;
using System.Text;

namespace Gpd.Core.Reporting;

/// <summary>
/// CSV 报告：一次写出两个文件。
/// <list type="bullet">
///   <item><c>诊断报告_&lt;进程名&gt;_&lt;时间戳&gt;_samples.csv</c>：一行一个采样点，表头用英文列名，紧跟一行以 <c>#</c> 开头的中文列说明。</item>
///   <item><c>诊断报告_&lt;进程名&gt;_&lt;时间戳&gt;_findings.csv</c>：诊断结论表。</item>
/// </list>
/// <see cref="Write"/> 返回 samples 文件的绝对路径，findings 文件在同一个目录下（名字可用 <see cref="FindingsFileName"/> 得到）。
/// 空单元格表示该项本机采集不到，而不是 0。文件带 UTF-8 BOM，Excel 直接双击不会乱码。
/// </summary>
public sealed class CsvReportWriter : IReportWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public string Write(DiagnosisReport report, string outputDirectory, ReportFormat format)
    {
        var (samples, _) = WriteAll(report, outputDirectory);
        return samples;
    }

    /// <summary>写出 samples 与 findings 两个 CSV，返回它们的绝对路径。</summary>
    public (string Samples, string Findings) WriteAll(DiagnosisReport report, string? outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);

        var directory = ReportFileHelper.EnsureDirectory(outputDirectory);
        var samplesPath = Path.Combine(directory, ReportFileHelper.BuildFileName(report, "_samples", "csv"));
        var findingsPath = Path.Combine(directory, FindingsFileName(report));

        File.WriteAllText(samplesPath, BuildSamplesCsv(report), Utf8WithBom);
        File.WriteAllText(findingsPath, BuildFindingsCsv(report), Utf8WithBom);

        return (Path.GetFullPath(samplesPath), Path.GetFullPath(findingsPath));
    }

    /// <summary>结论表文件名（不含目录）。</summary>
    public static string FindingsFileName(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return ReportFileHelper.BuildFileName(report, "_findings", "csv");
    }

    /// <summary>采样点 CSV 文本。</summary>
    public static string BuildSamplesCsv(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("elapsed_seconds,timestamp,cpu_total_percent,cpu_frequency_mhz,cpu_performance_percent,cpu_utility_percent,")
          .Append("cpu_package_temp_c,cpu_package_power_w,cpu_logical_count,cpu_per_core_percent,")
          .Append("gpu_name,gpu_util_percent,gpu_clock_mhz,gpu_mem_clock_mhz,gpu_temp_c,gpu_voltage_mv,gpu_power_w,gpu_power_limit_w,")
          .Append("gpu_fan_percent,gpu_process_dedicated_memory_mb,gpu_process_shared_memory_mb,gpu_total_dedicated_memory_mb,gpu_total_dedicated_memory_limit_mb,")
          .Append("process_working_set_mb,process_private_memory_mb,process_cpu_percent,")
          .Append("system_available_memory_mb,system_committed_percent,page_file_percent,disk_percent,disk_queue_length,")
          .Append("fps_source,fps,fps_average,frame_time_ms,fps_1percent_low,fps_01percent_low,notes")
          .Append('\n');

        sb.Append("# 列说明：elapsed_seconds=距采样开始的秒数；timestamp=本机时间；cpu_total_percent=整机 CPU 占用(%)；")
          .Append("cpu_frequency_mhz=CPU 当前频率(MHz)；cpu_performance_percent=性能计数器口径 CPU 占用(%)；cpu_utility_percent=Utility 口径 CPU 占用(%)；")
          .Append("cpu_package_temp_c=CPU 封装温度(℃)；cpu_package_power_w=CPU 封装功耗(W)；cpu_logical_count=逻辑处理器数；cpu_per_core_percent=逐逻辑核占用(%)，用 | 分隔；")
          .Append("gpu_name=显卡名；gpu_util_percent=显卡占用(%)；gpu_clock_mhz=核心频率(MHz)；gpu_mem_clock_mhz=显存频率(MHz)；gpu_temp_c=显卡温度(℃)；")
          .Append("gpu_voltage_mv=核心电压(mV)；gpu_power_w=显卡功耗(W)；gpu_power_limit_w=显卡功耗上限(W)；gpu_fan_percent=风扇转速(%)；")
          .Append("gpu_process_dedicated_memory_mb=本进程占用独显内存(MB)；gpu_process_shared_memory_mb=本进程占用共享内存(MB)；")
          .Append("gpu_total_dedicated_memory_mb=整机已用独显内存(MB)；gpu_total_dedicated_memory_limit_mb=独显内存总量(MB)；")
          .Append("process_working_set_mb=进程工作集(MB)；process_private_memory_mb=进程私有内存(MB)；process_cpu_percent=进程 CPU 占用(%)，相对单核，可能超过 100；")
          .Append("system_available_memory_mb=系统可用物理内存(MB)；system_committed_percent=系统提交内存占用(%)；page_file_percent=页面文件占用(%)；")
          .Append("disk_percent=磁盘活动时间(%)；disk_queue_length=磁盘队列长度；fps_source=帧率来源；fps=瞬时帧率；fps_average=本段平均帧率；")
          .Append("frame_time_ms=本帧耗时(毫秒)；fps_1percent_low=1% Low 帧率；fps_01percent_low=0.1% Low 帧率；notes=采集备注，用 | 分隔；")
          .Append("空单元格=本机采集不到该项，不是 0。")
          .Append('\n');

        var samples = report.Session.Samples;
        if (samples is not null)
        {
            foreach (var sample in samples)
            {
                sb.Append(N(sample.ElapsedSeconds, 3)).Append(',')
                  .Append(sample.Timestamp == default ? string.Empty : sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(N(sample.CpuTotalPercent)).Append(',')
                  .Append(N(sample.CpuFrequencyMhz)).Append(',')
                  .Append(N(sample.CpuPerformancePercent)).Append(',')
                  .Append(N(sample.CpuUtilityPercent)).Append(',')
                  .Append(N(sample.CpuPackageTempC)).Append(',')
                  .Append(N(sample.CpuPackagePowerW)).Append(',')
                  .Append(sample.CpuLogicalCount.HasValue ? sample.CpuLogicalCount.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(',')
                  .Append(Csv(sample.CpuPerCorePercent is { Count: > 0 }
                      ? string.Join(" | ", sample.CpuPerCorePercent.Select(v => N(v, 1)))
                      : null)).Append(',')
                  .Append(Csv(sample.GpuName)).Append(',')
                  .Append(N(sample.GpuUtilPercent)).Append(',')
                  .Append(N(sample.GpuClockMhz)).Append(',')
                  .Append(N(sample.GpuMemClockMhz)).Append(',')
                  .Append(N(sample.GpuTempC)).Append(',')
                  .Append(N(sample.GpuVoltageMv)).Append(',')
                  .Append(N(sample.GpuPowerW)).Append(',')
                  .Append(N(sample.GpuPowerLimitW)).Append(',')
                  .Append(N(sample.GpuFanPercent)).Append(',')
                  .Append(N(sample.GpuProcessDedicatedMemoryMb)).Append(',')
                  .Append(N(sample.GpuProcessSharedMemoryMb)).Append(',')
                  .Append(N(sample.GpuTotalDedicatedMemoryMb)).Append(',')
                  .Append(N(sample.GpuTotalDedicatedMemoryLimitMb)).Append(',')
                  .Append(N(sample.ProcessWorkingSetMb)).Append(',')
                  .Append(N(sample.ProcessPrivateMemoryMb)).Append(',')
                  .Append(N(sample.ProcessCpuPercent)).Append(',')
                  .Append(N(sample.SystemAvailableMemoryMb)).Append(',')
                  .Append(N(sample.SystemCommittedPercent)).Append(',')
                  .Append(N(sample.PageFilePercent)).Append(',')
                  .Append(N(sample.DiskPercent)).Append(',')
                  .Append(N(sample.DiskQueueLength, 3)).Append(',')
                  .Append(Csv(sample.FpsSource)).Append(',')
                  .Append(N(sample.Fps)).Append(',')
                  .Append(N(sample.FpsAverage)).Append(',')
                  .Append(N(sample.FrameTimeMs, 3)).Append(',')
                  .Append(N(sample.Fps1PercentLow)).Append(',')
                  .Append(N(sample.Fps01PercentLow)).Append(',')
                  .Append(Csv(sample.Notes is { Count: > 0 } ? string.Join(" | ", sample.Notes) : null))
                  .Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>结论 CSV 文本。</summary>
    public static string BuildFindingsCsv(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(8 * 1024);
        sb.Append("index,severity,severity_zh,category,title,evidence,explanation,recommendations,expected_gain,config_evidence").Append('\n');
        sb.Append("# 列说明：index=序号，按严重度从高到低；severity=严重度英文名(Info/Good/Minor/Warning/Critical)；severity_zh=严重度中文；")
          .Append("category=结论类别；title=结论标题；evidence=实测依据，用 | 分隔；explanation=成因解读；recommendations=建议，用 | 分隔；")
          .Append("expected_gain=预期效果；config_evidence=配置证据，用 | 分隔。空单元格=该项缺失。")
          .Append('\n');

        var findings = report.Findings;
        if (findings is not null)
        {
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Csv(f.Severity.ToString())).Append(',')
                  .Append(Csv(SeverityZh(f.Severity))).Append(',')
                  .Append(Csv(f.Category)).Append(',')
                  .Append(Csv(f.Title)).Append(',')
                  .Append(Csv(Join(f.Evidence))).Append(',')
                  .Append(Csv(f.Explanation)).Append(',')
                  .Append(Csv(Join(f.Recommendations))).Append(',')
                  .Append(Csv(f.ExpectedGain)).Append(',')
                  .Append(Csv(Join(f.ConfigEvidence)))
                  .Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string SeverityZh(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "严重",
        FindingSeverity.Warning => "警告",
        FindingSeverity.Minor => "轻微",
        FindingSeverity.Info => "提示",
        FindingSeverity.Good => "良好",
        _ => "提示",
    };

    private static string? Join(List<string>? values) =>
        values is { Count: > 0 } ? string.Join(" | ", values.Where(v => !string.IsNullOrWhiteSpace(v))) : null;

    private static string N(double? value, int digits = 2) =>
        value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? Math.Round(value.Value, digits, MidpointRounding.AwayFromZero).ToString("0." + new string('#', digits), CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',')
            || value.Contains('"')
            || value.Contains('\n')
            || value.Contains('\r')
            || value.StartsWith(' ')
            || value.EndsWith(' ')
                ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : value;
    }
}
