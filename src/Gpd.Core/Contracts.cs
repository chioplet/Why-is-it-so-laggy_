// ============================================================================
//  Gpd.Core / Contracts.cs
//  ---------------------------------------------------------------------------
//  这是本项目的【冻结契约文件】。
//  所有模块（采集 / 游戏库识别 / 配置分析 / 报告 / 界面）都只依赖本文件里的类型。
//  除非由主程统一修改，任何人不得改动本文件的既有成员签名；
//  需要新增字段时，只允许在末尾追加可选成员（不要改动已有成员的顺序/类型/名称）。
// ============================================================================

using System.Text.Json.Serialization;

namespace Gpd.Core;

/// <summary>单次性能采样的一个时间点。所有可为空的指标表示"本机/本进程采集不到"。</summary>
public sealed class PerformanceSample
{
    /// <summary>相对采样开始的秒数（第 0 秒为采样起点）。</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>采样时刻的本地时间。</summary>
    public DateTimeOffset Timestamp { get; set; }

    // ---- CPU ----
    /// <summary>CPU 总占用率 %（0-100，可能超过 100 在部分计数器上，按实际写入）。</summary>
    public double? CpuTotalPercent { get; set; }
    /// <summary>CPU 真实运行频率 MHz（来自 \Processor Information(_Total)\Processor Frequency）。</summary>
    public double? CpuFrequencyMhz { get; set; }
    /// <summary>CPU 性能百分比 %（\Processor Information(_Total)\% Processor Performance，100 表示跑在标称频率）。</summary>
    public double? CpuPerformancePercent { get; set; }
    /// <summary>CPU 处理器效能 %（\% Processor Utility）。</summary>
    public double? CpuUtilityPercent { get; set; }
    /// <summary>CPU 封装温度 ℃（能取到才有值）。</summary>
    public double? CpuPackageTempC { get; set; }
    /// <summary>CPU 封装功耗 W（能取到才有值）。</summary>
    public double? CpuPackagePowerW { get; set; }
    /// <summary>CPU 逻辑处理器个数。</summary>
    public int? CpuLogicalCount { get; set; }
    /// <summary>逐核/逐逻辑处理器占用率 %，索引 0 起。</summary>
    public List<double>? CpuPerCorePercent { get; set; }
    /// <summary>逐核温度 ℃，键为核序号（能取到才有值）。</summary>
    public Dictionary<int, double>? CpuCoreTempC { get; set; }

    // ---- GPU ----
    /// <summary>GPU 名称。</summary>
    public string? GpuName { get; set; }
    /// <summary>GPU 3D 引擎占用率 %（0-100）。</summary>
    public double? GpuUtilPercent { get; set; }
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuClockMhz { get; set; }
    /// <summary>GPU 显存频率 MHz。</summary>
    public double? GpuMemClockMhz { get; set; }
    /// <summary>GPU 温度 ℃。</summary>
    public double? GpuTempC { get; set; }
    /// <summary>GPU 电压 mV（nvidia-smi 报的是 mV）。</summary>
    public double? GpuVoltageMv { get; set; }
    /// <summary>GPU 板卡功耗 W。</summary>
    public double? GpuPowerW { get; set; }
    /// <summary>GPU 功耗上限 W。</summary>
    public double? GpuPowerLimitW { get; set; }
    /// <summary>GPU 风扇转速 %（能取到才有值）。</summary>
    public double? GpuFanPercent { get; set; }
    /// <summary>本进程在 GPU 上的专用显存占用 MB。</summary>
    public double? GpuProcessDedicatedMemoryMb { get; set; }
    /// <summary>本进程在 GPU 上的共享显存占用 MB。</summary>
    public double? GpuProcessSharedMemoryMb { get; set; }
    /// <summary>GPU 整体专用显存占用 MB。</summary>
    public double? GpuTotalDedicatedMemoryMb { get; set; }
    /// <summary>GPU 整体专用显存上限 MB。</summary>
    public double? GpuTotalDedicatedMemoryLimitMb { get; set; }

    // ---- 内存 / 存储 ----
    /// <summary>本进程工作集 MB。</summary>
    public double? ProcessWorkingSetMb { get; set; }
    /// <summary>本进程私有内存 MB。</summary>
    public double? ProcessPrivateMemoryMb { get; set; }
    /// <summary>本进程 CPU 占用率 %（相对单核，可能 >100）。</summary>
    public double? ProcessCpuPercent { get; set; }
    /// <summary>系统可用物理内存 MB。</summary>
    public double? SystemAvailableMemoryMb { get; set; }
    /// <summary>系统提交内存占用 %。</summary>
    public double? SystemCommittedPercent { get; set; }
    /// <summary>页面文件占用 %。</summary>
    public double? PageFilePercent { get; set; }
    /// <summary>物理磁盘总占用 %（\PhysicalDisk(_Total)\% Disk Time）。</summary>
    public double? DiskPercent { get; set; }
    /// <summary>平均磁盘队列长度。</summary>
    public double? DiskQueueLength { get; set; }

    // ---- 帧率（可选模块，采集不到时全部为 null）----
    /// <summary>帧率来源说明，例如 "PresentMon" / "FvSDK" / null。</summary>
    public string? FpsSource { get; set; }
    /// <summary>瞬时 FPS。</summary>
    public double? Fps { get; set; }
    /// <summary>本轮窗口内的平均 FPS。</summary>
    public double? FpsAverage { get; set; }
    /// <summary>帧生成时间 ms（1000/FPS）。</summary>
    public double? FrameTimeMs { get; set; }
    /// <summary>本轮窗口内 1% Low FPS。</summary>
    public double? Fps1PercentLow { get; set; }
    /// <summary>本轮窗口内 0.1% Low FPS（卡顿敏感指标）。</summary>
    public double? Fps01PercentLow { get; set; }

    /// <summary>该时间点采集过程中发生的错误信息（成功则为空）。</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>逐核占用/温度的静态快照（用于报告表格）。</summary>
public sealed class CpuCoreSnapshot
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public double? UsagePercent { get; set; }
    public double? TempC { get; set; }
}

/// <summary>一次完整的采集结果。</summary>
public sealed class SampleSession
{
    /// <summary>被采样的目标进程名（不含扩展名）。</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>被采样的目标进程 Id（采样过程中若进程重启会记录到 Notes）。</summary>
    public int ProcessId { get; set; }
    /// <summary>目标进程可执行文件完整路径（取不到则为空）。</summary>
    public string ProcessPath { get; set; } = "";
    /// <summary>若目标是识别出的游戏，这里是对应的游戏信息。</summary>
    public GameInfo? Game { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    /// <summary>名义采样间隔（秒）。</summary>
    public double IntervalSeconds { get; set; } = 1.0;
    /// <summary>采样结束原因：Manual / Duration / ProcessExited / Error。</summary>
    public string StopReason { get; set; } = "";

    public List<PerformanceSample> Samples { get; set; } = new();

    /// <summary>静态机器信息（CPU 型号、核数、内存、GPU、系统版本等）。</summary>
    public MachineInfo Machine { get; set; } = new();
    /// <summary>采集期间产生的全局告警/说明。</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>有效帧率样本数（Fps 非 null 的样本个数）。</summary>
    [JsonIgnore]
    public int FpsSampleCount => Samples.Count(s => s.Fps.HasValue);
}

/// <summary>静态机器/系统信息。</summary>
public sealed class MachineInfo
{
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string CpuName { get; set; } = "";
    public int CpuPhysicalCores { get; set; }
    public int CpuLogicalProcessors { get; set; }
    public double CpuNominalMhz { get; set; }
    public double TotalMemoryMb { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();
    public bool IsElevated { get; set; }
    public string PowerPlanName { get; set; } = "";
    public bool? GameModeEnabled { get; set; }
    public bool? HardwareGpuSchedulingEnabled { get; set; }
    public string NvidiaDriverVersion { get; set; } = "";
    /// <summary>本机可用的采集能力探测结果（供报告说明"为什么某项没有数据"）。</summary>
    public List<CapabilityProbe> Capabilities { get; set; } = new();
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public double? VramMb { get; set; }
    public string DriverVersion { get; set; } = "";
    public bool IsVirtualDisplayAdapter { get; set; }
}

/// <summary>某项采集能力的可用性探测结果。</summary>
public sealed class CapabilityProbe
{
    public string Name { get; set; } = "";
    public bool Available { get; set; }
    /// <summary>不可用时的原因（要写进报告，方便用户理解）。</summary>
    public string Detail { get; set; } = "";
}

// ============================================================================
//  游戏识别
// ============================================================================

public enum GamePlatform
{
    Unknown = 0,
    Steam,
    Epic,
    BattleNet,
    WeGame,
    Gog,
    Xbox,
    Ubisoft,
    Standalone,
    Manual,
}

/// <summary>一个被识别出的游戏。</summary>
public sealed class GameInfo
{
    public string Name { get; set; } = "";
    public GamePlatform Platform { get; set; } = GamePlatform.Unknown;
    /// <summary>平台显示名，例如 "Steam"。</summary>
    public string PlatformName => Platform switch
    {
        GamePlatform.Steam => "Steam",
        GamePlatform.Epic => "Epic Games",
        GamePlatform.BattleNet => "Battle.net",
        GamePlatform.WeGame => "WeGame",
        GamePlatform.Gog => "GOG",
        GamePlatform.Xbox => "Xbox",
        GamePlatform.Ubisoft => "Ubisoft Connect",
        GamePlatform.Standalone => "独立安装",
        GamePlatform.Manual => "手动选择",
        _ => "未知",
    };

    /// <summary>安装目录（可能为空）。</summary>
    public string InstallDir { get; set; } = "";
    /// <summary>主可执行文件完整路径（可能为空）。</summary>
    public string ExecutablePath { get; set; } = "";
    /// <summary>进程名（不含扩展名，用于匹配运行中的进程）。</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>平台内的 AppId / 产品 Id。</summary>
    public string AppId { get; set; } = "";
    /// <summary>最近一次运行时间（能取到才有值）。</summary>
    public DateTimeOffset? LastPlayed { get; set; }
    /// <summary>累计游玩时长（能取到才有值）。</summary>
    public TimeSpan? PlayTime { get; set; }
    /// <summary>找到的画质/设置配置文件。</summary>
    public List<ConfigFile> ConfigFiles { get; set; } = new();
    /// <summary>该游戏的识别来源说明（写进报告）。</summary>
    public string DiscoverySource { get; set; } = "";
}

/// <summary>游戏配置文件（只读读取，绝不修改）。</summary>
public sealed class ConfigFile
{
    public string Path { get; set; } = "";
    /// <summary>解析格式：ini / json / xml / cfg / txt / binary。</summary>
    public string Format { get; set; } = "";
    /// <summary>人类可读的用途说明，例如 "图形设置" / "引擎配置"。</summary>
    public string Purpose { get; set; } = "";
    /// <summary>解析出的键值对（只读快照）。值统一转成字符串。</summary>
    public List<ConfigEntry> Entries { get; set; } = new();
    /// <summary>读取失败时的原因。</summary>
    public string Error { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset? LastWriteTime { get; set; }
}

public sealed class ConfigEntry
{
    /// <summary>节名（ini 的 [Section]，json 的父对象路径）。</summary>
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>对画质配置的解读结果（不是原始键值，而是"这意味着什么"）。</summary>
public sealed class GraphicsSettingInsight
{
    /// <summary>设置项的人类可读名，例如 "阴影质量"。</summary>
    public string SettingName { get; set; } = "";
    /// <summary>原始键名（含节名）。</summary>
    public string SourceKey { get; set; } = "";
    /// <summary>原始值。</summary>
    public string RawValue { get; set; } = "";
    /// <summary>归一化后的档位描述，例如 "高" / "超高" / "关闭" / "1080p"。</summary>
    public string NormalizedValue { get; set; } = "";
    /// <summary>对性能的影响程度 0-5，越大越吃性能。</summary>
    public int PerformanceImpact { get; set; }
    /// <summary>解释与建议。</summary>
    public string Comment { get; set; } = "";
}

// ============================================================================
//  诊断与建议
// ============================================================================

public enum FindingSeverity
{
    Info = 0,
    Good,
    Minor,
    Warning,
    Critical,
}

/// <summary>一条诊断结论 / 优化建议。</summary>
public sealed class Finding
{
    /// <summary>结论标题，例如 "GPU 是瓶颈"。</summary>
    public string Title { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    /// <summary>分类，例如 "性能瓶颈" / "卡顿" / "画质设置" / "系统设置" / "后台干扰"。</summary>
    public string Category { get; set; } = "";
    /// <summary>证据：支撑该结论的实测数据（报告里会原样展示）。</summary>
    public List<string> Evidence { get; set; } = new();
    /// <summary>结论说明：为什么会这样。</summary>
    public string Explanation { get; set; } = "";
    /// <summary>改进方法（可执行的具体步骤）。</summary>
    public List<string> Recommendations { get; set; } = new();
    /// <summary>预期收益（可空，要诚实，不要编造百分比）。</summary>
    public string ExpectedGain { get; set; } = "";
    /// <summary>相关配置证据（例如某画质项当前是什么）。</summary>
    public List<string> ConfigEvidence { get; set; } = new();
}

/// <summary>报告的完整分析结果。</summary>
public sealed class DiagnosisReport
{
    public SampleSession Session { get; set; } = new();
    public GameInfo? Game { get; set; }

    /// <summary>汇总指标（供报告开头展示）。</summary>
    public PerformanceSummary Summary { get; set; } = new();
    /// <summary>画质设置解读。</summary>
    public List<GraphicsSettingInsight> GraphicsInsights { get; set; } = new();
    /// <summary>逐核快照。</summary>
    public List<CpuCoreSnapshot> Cores { get; set; } = new();
    /// <summary>诊断结论与建议，按严重程度从高到低排列。</summary>
    public List<Finding> Findings { get; set; } = new();

    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public string ToolVersion { get; set; } = "";
}

/// <summary>整段采样的统计摘要。</summary>
public sealed class PerformanceSummary
{
    public double DurationSeconds { get; set; }
    public int SampleCount { get; set; }

    public double? CpuAvgPercent { get; set; }
    public double? CpuMaxPercent { get; set; }
    public double? CpuAvgFrequencyMhz { get; set; }
    public double? CpuMaxFrequencyMhz { get; set; }
    public double? CpuAvgTempC { get; set; }
    public double? CpuMaxTempC { get; set; }

    /// <summary>
    /// CPU 温度读数是否真的跟着负载走，也就是它到底能不能当「CPU 封装温度」用。
    /// <list type="bullet">
    /// <item><c>true</c>：高负载时的读数明显高于低负载时，可以按 CPU 温度解读。</item>
    /// <item><c>false</c>：读数与负载无关。实测本机（i7-13620H + RTX 4060 Laptop）就是这样：
    /// ACPI 热区 <c>\_TZ.TZ0</c> 空闲 85.1℃、16 线程满载反而 82.7℃。CPU 封装温度满载不可能下降，
    /// 所以它只是个主板/机壳热区，不能据此判断 CPU 是否过热。</item>
    /// <item><c>null</c>：本次采样的负载跨度不够（没有同时出现足够多的低负载与高负载样本），无法判断。</item>
    /// </list>
    /// 报告层必须据此降级措辞：不是 <c>true</c> 时不能说「CPU 温度正常」，只能说「热区温度」。
    /// </summary>
    public bool? CpuTempTracksLoad { get; set; }
    public double? CpuAvgPowerW { get; set; }
    public double? CpuMaxPowerW { get; set; }

    public double? GpuAvgUtilPercent { get; set; }
    public double? GpuMaxUtilPercent { get; set; }
    public double? GpuAvgClockMhz { get; set; }
    public double? GpuMaxClockMhz { get; set; }
    public double? GpuAvgTempC { get; set; }
    public double? GpuMaxTempC { get; set; }
    public double? GpuAvgPowerW { get; set; }
    public double? GpuMaxPowerW { get; set; }
    public double? GpuPowerLimitW { get; set; }
    /// <summary>功耗受限（达到上限 95% 以上）的样本占比 %。</summary>
    public double? GpuPowerLimitedPercent { get; set; }
    /// <summary>温度达到 83℃ 以上的样本占比 %（移动端降频高发区）。</summary>
    public double? GpuThermalRiskPercent { get; set; }

    public double? ProcessAvgWorkingSetMb { get; set; }
    public double? ProcessMaxWorkingSetMb { get; set; }
    public double? SystemMinAvailableMemoryMb { get; set; }
    public double? PageFileMaxPercent { get; set; }
    public double? DiskAvgPercent { get; set; }
    /// <summary>物理磁盘占用峰值 %。磁盘 100% 尖峰是卡顿的常见直接原因，报告要单独列。</summary>
    public double? DiskMaxPercent { get; set; }
    public double? DiskMaxQueueLength { get; set; }

    public double? FpsAvg { get; set; }
    public double? FpsMin { get; set; }
    public double? FpsMax { get; set; }
    public double? Fps1PercentLow { get; set; }
    public double? Fps01PercentLow { get; set; }
    /// <summary>帧生成时间 P99（ms）。</summary>
    public double? FrameTimeP99Ms { get; set; }
    /// <summary>帧生成时间标准差（ms）——抖动指标。</summary>
    public double? FrameTimeStdDevMs { get; set; }
    /// <summary>判定为"卡顿帧"的样本数（帧时间 > 2× 中位数）。</summary>
    public int StutterCount { get; set; }
    /// <summary>每分钟卡顿次数。</summary>
    public double? StuttersPerMinute { get; set; }
    /// <summary>CPU 与 GPU 占用率差值绝对值，用于判断瓶颈。</summary>
    public double? CpuGpuGapPercent { get; set; }
    /// <summary>瓶颈判定：CPU / GPU / 均衡 / 未知。</summary>
    public string BottleneckVerdict { get; set; } = "未知";
}

// ============================================================================
//  采集接口
// ============================================================================

/// <summary>某个采集模块的能力与状态。</summary>
public sealed class CollectorStatus
{
    public string Name { get; set; } = "";
    public bool Available { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>
/// 数据采集器。实现类必须线程安全地支持 <see cref="Start"/> / <see cref="Collect"/> / <see cref="Stop"/>，
/// 并且【任何异常都不能向外抛】——采集失败时把原因写进 <see cref="PerformanceSample.Notes"/>。
/// </summary>
public interface IPerformanceCollector : IDisposable
{
    string Name { get; }
    /// <summary>探测本机是否可用（在 UI 上显示，并写进报告）。</summary>
    CollectorStatus Probe();
    /// <summary>开始采集前的准备（打开计数器等）。targetProcessId 为目标游戏进程。</summary>
    void Start(int targetProcessId, string targetProcessName);
    /// <summary>采集一个时间点。所有字段都要尽量填，采不到的留 null 并写 Notes。</summary>
    void Collect(PerformanceSample sample);
    /// <summary>结束采集，释放句柄。</summary>
    void Stop();
}

/// <summary>帧率采集器（可选模块）。</summary>
public interface IFpsCollector : IPerformanceCollector
{
    /// <summary>该模块是否需要管理员权限。</summary>
    bool RequiresElevation { get; }
}

/// <summary>报告输出格式。</summary>
public enum ReportFormat
{
    Markdown,
    Pdf,
    Csv,
    Json,
}

/// <summary>报告输出器。</summary>
public interface IReportWriter
{
    /// <summary>把诊断结果写成指定格式，返回写出的文件路径。</summary>
    string Write(DiagnosisReport report, string outputDirectory, ReportFormat format);
}

/// <summary>游戏库扫描器。</summary>
public interface IGameLibraryScanner
{
    string Name { get; }
    /// <summary>扫描本平台已安装的游戏。失败时返回空列表并写 <paramref name="warnings"/>。</summary>
    IEnumerable<GameInfo> Scan(List<string> warnings);
}

/// <summary>配置文件解析器。</summary>
public interface IConfigParser
{
    /// <summary>本解析器能否处理该文件。</summary>
    bool CanParse(string filePath);
    /// <summary>只读解析。绝不允许写入或修改文件。</summary>
    ConfigFile Parse(string filePath);
}
