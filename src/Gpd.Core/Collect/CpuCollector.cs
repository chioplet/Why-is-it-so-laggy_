using System.Diagnostics;

namespace Gpd.Core.Collect;

/// <summary>
/// CPU 采集器：占用率、真实运行频率、逐核占用、逐核温度、封装温度与功耗。
/// </summary>
/// <remarks>
/// 数据来源与实测说明（Windows 11 build 26300，中文系统上英文计数器名有效）：
///   \Processor Information(_Total)\% Processor Time          → 总占用率
///   \Processor Information(_Total)\Actual Frequency          → **当前真实频率 MHz**（首选）
///   \Processor Information(_Total)\% Processor Performance   → 相对标称频率的百分比，乘标称频率即真实频率（次选）
///   \Processor Information(_Total)\Processor Frequency       → **只是静态标称基频，不随负载变化，不可当当前频率用**
///   \Processor Information(_Total)\% Processor Utility        → 效能占用
///   \Processor Information(&lt;n&gt;,0)\% Processor Time           → 逐逻辑处理器占用
///   \Thermal Zone Information(\_TZ.TZ0)\High Precision Temperature → 热区温度（开尔文 ×10）
///   \Energy Meter(RAPL_Package0_PKG)\Power                   → CPU 封装功耗（毫瓦）
/// 拿不到的项一律留 null，并在 sample.Notes 里说明原因。
/// </remarks>
public sealed class CpuCollector : IPerformanceCollector
{
    public string Name => "CPU（性能计数器）";

    private PerformanceCounter? _totalTime;
    private PerformanceCounter? _perfPercent;
    private PerformanceCounter? _utility;
    private PerformanceCounter? _frequency;
    private PerformanceCounter? _actualFrequency;

    private readonly List<PerformanceCounter> _coreTime = new();

    /// <summary>逐热区温度。两个温度计数器的单位不同，必须分别保存。</summary>
    private readonly List<ThermalZone> _thermal = new();

    /// <summary>CPU 封装功耗（Energy Meter → RAPL_Package0_PKG）。Windows 该计数器单位是毫瓦。</summary>
    private PerformanceCounter? _pkgPower;

    private string[] _coreLabels = Array.Empty<string>();

    /// <summary>一个热区及其两个温度计数器。</summary>
    private sealed class ThermalZone
    {
        public string Instance = "";
        public PerformanceCounter? Kelvin;      // "Temperature"：开尔文原值
        public PerformanceCounter? Precise;     // "High Precision Temperature"：开尔文 × 10
    }

    /// <summary>CPU 标称频率 MHz（用于在只有 % Processor Performance 时换算真实频率）。</summary>
    public double NominalMhz { get; set; }

    private int _processId;
    private string _processName = "";

    /// <summary>进程级 CPU 占用：需要两次采样求差，这里保存上次的处理器时间。</summary>
    private TimeSpan _lastProcCpuTime;
    private DateTime _lastProcSample = DateTime.MinValue;

    private readonly List<string> _probeNotes = new();

    public CollectorStatus Probe()
    {
        var status = new CollectorStatus { Name = Name };
        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information"))
            {
                status.Available = false;
                status.Detail = "找不到性能计数器类别 Processor Information（系统计数器可能被禁用，可运行 lodctr /R 重建）";
                return status;
            }
            using var c = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
            _ = c.NextValue();
            status.Available = true;

            var details = new List<string> { "占用率 / 逐核占用 可用" };

            try
            {
                using var p = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
                p.NextValue(); Thread.Sleep(120); double v = p.NextValue();
                details.Add($"真实频率可用（% Processor Performance = {v:F1}%，标称 {NominalMhz:F0}MHz）");
            }
            catch (Exception ex) { details.Add($"真实频率不可用（{ex.Message}）"); }

            try
            {
                using var f = new PerformanceCounter("Processor Information", "Actual Frequency", "_Total", readOnly: true);
                f.NextValue(); Thread.Sleep(120); double af = f.NextValue();
                details.Add($"实际频率可用（Actual Frequency = {af:F0}MHz）");
            }
            catch { details.Add("Actual Frequency 不可用（将用 % Processor Performance × 标称频率换算）"); }

            // 【实测教训 · 重要】"Processor Frequency" 是**静态标称基频**，不随负载变化：
            // 本机 _Total 恒为 2250MHz、P 核恒 2400、E 核恒 1800（就是 i7-13620H 的基频），
            // 空闲和满载读数完全一样。**不能拿它当"当前频率"**，否则报告里 CPU 频率永远是常数。
            // 真正的当前频率是 "Actual Frequency"（空闲 3.6-4.1GHz，满载掉到 ~3.0GHz）。
            try
            {
                using var bf = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", readOnly: true);
                bf.NextValue();
                details.Add("Processor Frequency 可读，但它只是标称基频（不随负载变化），仅作参考");
            }
            catch { /* 无关紧要 */ }

            try
            {
                if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
                {
                    // 【实测教训】Thermal Zone Information 只有一个实例 "\_TZ.TZ0"，
                    // **不存在 _Total**（之前用 _Total 必然抛异常，温度恒为 null）。
                    var inst = new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames();
                    if (inst.Length == 0) details.Add("温度不可用：Thermal Zone Information 没有任何实例");
                    else
                    {
                        using var t = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", inst[0], readOnly: true);
                        t.NextValue();
                        details.Add($"热区温度可读（ACPI 热区 {string.Join("/", inst)}，当前{(CollectUtil.IsElevated() ? "已是管理员" : "非管理员，部分热区可能被拒")}）；"
                                  + "注意这是主板 ACPI 热区，不保证等于 CPU 封装温度，报告会另做负载相关性判定");
                    }
                }
                else details.Add("温度不可用：本机没有 Thermal Zone Information 计数器");
            }
            catch (Exception ex) { details.Add($"温度不可用（{ex.Message}）"); }

            // 【实测教训】CPU 封装功耗本来就能读，不需要 Intel Power Gadget / HWiNFO：
            // Energy Meter 有 5 个实例 RAPL_Package0_PKG / _PP0 / _PP1 / _DRAM / _Total，
            // 其中 **只有 RAPL_Package0_PKG 有意义（_Total 恒为 0）**，单位是毫瓦。
            try
            {
                if (PerformanceCounterCategory.Exists("Energy Meter"))
                {
                    var inst = new PerformanceCounterCategory("Energy Meter").GetInstanceNames();
                    if (inst.Any(n => n.Equals("RAPL_Package0_PKG", StringComparison.OrdinalIgnoreCase)))
                        details.Add("封装功耗可用（Energy Meter → RAPL_Package0_PKG，单位 mW 需 ÷1000）");
                    else
                        details.Add($"功耗不可用：Energy Meter 存在但没有 RAPL_Package0_PKG 实例（现有 {string.Join("/", inst)}）");
                }
                else details.Add("功耗不可用：本机没有 Energy Meter 计数器（部分平台不支持 RAPL 能耗上报）");
            }
            catch (Exception ex) { details.Add($"功耗探测异常（{ex.Message}）"); }

            status.Detail = string.Join("；", details);
        }
        catch (Exception ex)
        {
            status.Available = false;
            status.Detail = ex.Message;
        }
        return status;
    }

    public void Start(int targetProcessId, string targetProcessName)
    {
        _processId = targetProcessId;
        _processName = targetProcessName;
        _probeNotes.Clear();

        TryCreate("Processor Information", "% Processor Time", "_Total", ref _totalTime, "总占用率");
        TryCreate("Processor Information", "% Processor Performance", "_Total", ref _perfPercent, "% Processor Performance（真实频率）");
        TryCreate("Processor Information", "% Processor Utility", "_Total", ref _utility, "% Processor Utility");
        TryCreate("Processor Information", "Processor Frequency", "_Total", ref _frequency, "Processor Frequency");
        TryCreate("Processor Information", "Actual Frequency", "_Total", ref _actualFrequency, "Actual Frequency");

        // 预热：第一次 NextValue 总是 0
        foreach (var c in new[] { _totalTime, _perfPercent, _utility, _frequency, _actualFrequency })
        {
            try { c?.NextValue(); } catch { /* 忽略 */ }
        }

        // 逐逻辑处理器占用
        try
        {
            var cat = new PerformanceCounterCategory("Processor Information");
            var names = cat.GetInstanceNames()
                .Where(IsLogicalProcessorInstance)   // "0,0" / "0,8"，排除 "_Total" 和 "0,_Total"
                .OrderBy(n => ParseCoreTuple(n).group)
                .ThenBy(n => ParseCoreTuple(n).index)
                .ToArray();
            _coreLabels = names;
            foreach (var n in names)
            {
                var c = new PerformanceCounter("Processor Information", "% Processor Time", n, readOnly: true);
                c.NextValue();
                _coreTime.Add(c);
            }
        }
        catch (Exception ex)
        {
            _probeNotes.Add($"逐核占用采集不可用：{ex.Message}");
        }

        // 逐热区温度（两个计数器单位不同，都建上，采集时优先用精度高的那个）
        try
        {
            if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                foreach (var n in cat.GetInstanceNames())
                {
                    var zone = new ThermalZone { Instance = n };
                    try
                    {
                        zone.Precise = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", n, readOnly: true);
                        zone.Precise.NextValue();
                    }
                    catch { zone.Precise = null; }

                    try
                    {
                        zone.Kelvin = new PerformanceCounter("Thermal Zone Information", "Temperature", n, readOnly: true);
                        zone.Kelvin.NextValue();
                    }
                    catch { zone.Kelvin = null; }

                    if (zone.Precise is not null || zone.Kelvin is not null) _thermal.Add(zone);
                }
                if (_thermal.Count == 0) _probeNotes.Add("温度采集不可用：Thermal Zone Information 的实例上两个温度计数器都读不到。");
            }
        }
        catch (Exception ex)
        {
            _probeNotes.Add($"温度采集不可用：{ex.Message}");
        }

        // CPU 封装功耗（毫瓦）
        try
        {
            if (PerformanceCounterCategory.Exists("Energy Meter"))
            {
                var inst = new PerformanceCounterCategory("Energy Meter").GetInstanceNames()
                    .FirstOrDefault(n => n.Equals("RAPL_Package0_PKG", StringComparison.OrdinalIgnoreCase));
                if (inst is not null)
                {
                    _pkgPower = new PerformanceCounter("Energy Meter", "Power", inst, readOnly: true);
                    _pkgPower.NextValue();
                }
                else _probeNotes.Add("CPU 功耗采集不可用：Energy Meter 里没有 RAPL_Package0_PKG 实例。");
            }
        }
        catch (Exception ex)
        {
            _probeNotes.Add($"CPU 功耗采集不可用：{ex.Message}");
        }

        // 进程级 CPU 时间基线
        try
        {
            using var p = Process.GetProcessById(_processId);
            _lastProcCpuTime = p.TotalProcessorTime;
            _lastProcSample = DateTime.UtcNow;
        }
        catch { /* 进程可能已退出，后面每轮再试 */ }
    }

    /// <summary>把热区读数换成摄氏度。优先用高精度计数器（K×10），退回温度计数器（开尔文原值）。</summary>
    private double? ConvertToCelsius(ThermalZone zone)
    {
        var precise = ReadNext(zone.Precise);
        if (precise is > 0)
        {
            var c = precise.Value / 10.0 - 273.15;
            if (c is > -20 and < 150) return Math.Round(c, 1);
        }

        var kelvin = ReadNext(zone.Kelvin);
        if (kelvin is > 0)
        {
            var c = kelvin.Value - 273.15;
            if (c is > -20 and < 150) return Math.Round(c, 1);
        }
        return null;
    }

    private static (int group, int index) ParseCoreTuple(string instance)
    {
        var parts = instance.Split(',');
        int g = 0, i = 0;
        if (parts.Length == 2)
        {
            int.TryParse(parts[0], out g);
            int.TryParse(parts[1], out i);
        }
        return (g, i);
    }

    /// <summary>
    /// 判断实例名是不是"逻辑处理器"（形如 <c>0,3</c>）。
    /// 【实测教训】Processor Information 一共 18 个实例：<c>0,0</c>…<c>0,15</c>（16 个逻辑处理器）、
    /// <c>_Total</c>、以及 <c>0,_Total</c>。只判断"含逗号"会把 <c>0,_Total</c> 也收进来，
    /// 解析失败后退化成 (0,0)，逐核数组长度就变成 17。必须要求两段都能解析成整数。
    /// </summary>
    private static bool IsLogicalProcessorInstance(string instance)
    {
        var parts = instance.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _);
    }

    private void TryCreate(string category, string counter, string instance, ref PerformanceCounter? field, string label)
    {
        try
        {
            field = new PerformanceCounter(category, counter, instance, readOnly: true);
        }
        catch (Exception ex)
        {
            field = null;
            _probeNotes.Add($"{label} 采集不可用：{ex.Message}");
        }
    }

    public void Collect(PerformanceSample sample)
    {
        foreach (var note in _probeNotes)
            if (!sample.Notes.Contains(note)) sample.Notes.Add(note);

        // ---- 总占用率 ----
        sample.CpuTotalPercent = ReadNext(_totalTime);

        // ---- 真实频率 ----
        double? perfPercent = ReadNext(_perfPercent);
        sample.CpuPerformancePercent = perfPercent;
        sample.CpuUtilityPercent = ReadNext(_utility);

        double? freq = ReadNext(_actualFrequency);
        if (freq is > 0)
        {
            sample.CpuFrequencyMhz = Math.Round(freq.Value, 0);
        }
        else if (perfPercent is > 0 && NominalMhz > 0)
        {
            // % Processor Performance 是相对标称频率的百分比。
            // 实测与 Actual Frequency 互证：满载 126.6% × 2400 = 3038MHz ≈ Actual Frequency 3039MHz。
            sample.CpuFrequencyMhz = Math.Round(perfPercent.Value / 100.0 * NominalMhz, 0);
        }

        // ---- 逐逻辑处理器 ----
        if (_coreTime.Count > 0)
        {
            var perCore = new List<double>(_coreTime.Count);
            foreach (var c in _coreTime)
            {
                var v = ReadNext(c);
                perCore.Add(v ?? 0);
            }
            sample.CpuPerCorePercent = perCore;
            sample.CpuLogicalCount = perCore.Count;
        }

        // ---- 温度 ----
        // 【实测教训 · 两个计数器单位不同，必须分开算】
        //   "Temperature"                  raw 358.0  → 开尔文原值，358.0 - 273.15 = 84.9℃
        //   "High Precision Temperature"   raw 3582.0 → 开尔文 × 10，3582/10 - 273.15 = 85.1℃
        // 之前一律按 K×10 算得到 -237.3℃，被合理区间过滤掉 → 温度永远是 null。
        if (_thermal.Count > 0)
        {
            var temps = new Dictionary<int, double>();
            for (int i = 0; i < _thermal.Count; i++)
            {
                var c = ConvertToCelsius(_thermal[i]);
                if (c is not null) temps[i] = c.Value;
            }
            if (temps.Count > 0)
            {
                sample.CpuCoreTempC = temps;
                // 取最高的热区作为"封装温度"的近似
                sample.CpuPackageTempC = temps.Values.Max();
            }
        }

        // ---- CPU 封装功耗（Energy Meter 的 Power 单位是毫瓦）----
        var mw = ReadNext(_pkgPower);
        if (mw is > 0) sample.CpuPackagePowerW = Math.Round(mw.Value / 1000.0, 1);

        // ---- 进程级 CPU 占用（相对单核，可 >100%）----
        try
        {
            using var p = Process.GetProcessById(_processId);
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            var elapsed = (now - _lastProcSample).TotalMilliseconds;
            if (elapsed > 50 && _lastProcSample != DateTime.MinValue)
            {
                var used = (cpu - _lastProcCpuTime).TotalMilliseconds;
                sample.ProcessCpuPercent = CollectUtil.Sanitize(Math.Round(used / elapsed * 100.0, 1));
            }
            _lastProcCpuTime = cpu;
            _lastProcSample = now;
        }
        catch (Exception ex)
        {
            sample.Notes.Add($"目标进程 CPU 占用读取失败：{ex.Message}");
        }
    }

    private static double? ReadNext(PerformanceCounter? c)
    {
        if (c is null) return null;
        try { return CollectUtil.Sanitize(c.NextValue()); }
        catch { return null; }
    }

    public void Stop()
    {
        foreach (var c in AllCounters()) { try { c?.Dispose(); } catch { /* 忽略 */ } }
        _totalTime = _perfPercent = _utility = _frequency = _actualFrequency = null;
        _pkgPower = null;
        _coreTime.Clear();
        _thermal.Clear();
    }

    private IEnumerable<PerformanceCounter?> AllCounters()
    {
        yield return _totalTime;
        yield return _perfPercent;
        yield return _utility;
        yield return _frequency;
        yield return _actualFrequency;
        yield return _pkgPower;
        foreach (var c in _coreTime) yield return c;
        foreach (var z in _thermal) { yield return z.Precise; yield return z.Kelvin; }
    }

    public void Dispose() => Stop();
}
