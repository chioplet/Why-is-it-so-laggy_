using System.Diagnostics;
using System.Globalization;

namespace Gpd.Core.Collect;

/// <summary>
/// GPU 采集器。主数据源是 nvidia-smi（NVIDIA 显卡）；
/// 进程显存占用与本进程 3D 引擎占用来自 Windows 性能计数器
/// <c>GPU Process Memory</c> / <c>GPU Engine</c>。
/// </summary>
/// <remarks>
/// 【实测教训 · 必须记住】
///  1. 本机 nvidia-smi 617.14 **不支持 <c>--format=xml</c>**：会以退出码 2 结束并只往 stdout
///     打一行 <c>Format modifier is not recognized.</c>。之前整套 XML 解析因此全线失效、
///     「识别到 0 张卡」。现在统一用 <c>--format=csv,noheader,nounits</c>。
///  2. 只要 <c>--query-gpu</c> 里有一个本驱动不认的字段，nvidia-smi 会拒绝**整条**查询
///     并把错误打到 **stdout**（例如 <c>Field "voltage.gpu" is not a valid field to query.</c>，
///     退出码 2）。本机驱动就不认 voltage.gpu。因此：只保留确定可用的字段，
///     电压单独探测后再决定是否加入；解析时**必须跳过非数据行**。
///  3. 单次调用耗时 47-147ms，非管理员可用。
///  4. 本机还装了 GameViewer Virtual Display Adapter 虚拟显示器，会让 GPU 计数器
///     出现额外实例（GPU Engine 共 348 个实例），报告里要标注这一情况。
/// </remarks>
public sealed class GpuCollector : IPerformanceCollector
{
    public string Name => "GPU（nvidia-smi + GPU 性能计数器）";

    private string? _nvidiaSmi;
    private bool _elevated;

    private readonly List<PerformanceCounter> _gpuEngine3D = new();      // 本进程 3D 引擎实例
    private readonly List<PerformanceCounter> _procMemDedicated = new();  // 本进程专用显存
    private readonly List<PerformanceCounter> _procMemShared = new();     // 本进程共享显存
    private readonly List<PerformanceCounter> _adapterMem = new();        // 全卡显存占用
    private readonly List<string> _notes = new();

    private int _processId;
    private string _processName = "";

    /// <summary>nvidia-smi 报出来的显卡列表。</summary>
    public List<GpuInfo> Gpus { get; } = new();

    /// <summary>采集到的显卡数量（供上层判断多卡）。</summary>
    public int GpuCount => Math.Max(1, Gpus.Count);

    public CollectorStatus Probe()
    {
        var status = new CollectorStatus { Name = Name };
        var details = new List<string>();

        _nvidiaSmi = CollectUtil.FindExecutable("nvidia-smi.exe");
        if (_nvidiaSmi is null)
        {
            status.Available = false;
            status.Detail = "找不到 nvidia-smi.exe（非 NVIDIA 显卡或驱动未安装），GPU 频率/温度/功耗/电压将无法采集。";
            return status;
        }
        details.Add($"nvidia-smi: {_nvidiaSmi}");

        var sw = Stopwatch.StartNew();
        var csv = CollectUtil.RunProcessUtf8(_nvidiaSmi, GpuQueryArgs(0), 5000, out var err);
        sw.Stop();
        if (string.IsNullOrWhiteSpace(csv))
        {
            status.Available = false;
            status.Detail = $"nvidia-smi 调用失败：{err}";
            return status;
        }
        var gpus = ParseGpus(csv, out var parseErr);
        details.Add($"nvidia-smi 响应 {sw.ElapsedMilliseconds}ms，识别到 {gpus.Count} 张卡");
        if (gpus.Count == 0) details.Add($"解析异常：{parseErr}");

        // 虚拟显示器探测：本机装了 GameViewer Virtual Display Adapter，会让 GPU 计数出现额外实例
        var virtualAdapters = DetectVirtualDisplayAdapters();
        if (virtualAdapters.Count > 0)
            details.Add($"检测到虚拟显示器适配器：{string.Join("、", virtualAdapters)}（会干扰部分 GPU 计数器的解读，但不影响 nvidia-smi）");

        _elevated = CollectUtil.IsElevated();
        if (!_elevated)
            details.Add("当前非管理员：进程级显存计数可能读不到（可用「以管理员身份运行」改善）");

        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            details.Add($"GPU Engine 计数器实例数 {cat.GetInstanceNames().Length}（含虚拟显示器）");
        }
        catch (Exception ex) { details.Add($"GPU Engine 计数器不可用：{ex.Message}"); }

        status.Available = gpus.Count > 0;
        status.Detail = string.Join("；", details);
        return status;
    }

    private static readonly string[] VirtualAdapterHints = { "GameViewer", "Virtual Display", "Idd", "Parsec", "Spacedesk" };

    private static List<string> DetectVirtualDisplayAdapters()
    {
        var found = new List<string>();
        try
        {
            const string key = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if (root is null) return found;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;
                if (VirtualAdapterHints.Any(h => desc!.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    found.Add(desc!);
            }
        }
        catch { /* 读不到就算了，不影响主流程 */ }
        return found;
    }

    /// <summary>查询参数。<c>nounits</c> 让数值不带单位（<c>32</c> 而不是 <c>32 %</c>），解析更干净。</summary>
    private static string GpuQueryArgs(int index)
        => "--query-gpu=" + string.Join(',', Fields) + $" --format=csv,noheader,nounits -i {index}";

    /// <summary>
    /// 实际查询的字段。【重要实测教训】只要列表里有一个本驱动不认的字段，
    /// nvidia-smi 会拒绝整条查询并把错误打到 stdout，于是所有 GPU 指标一起消失。
    /// 本机驱动 617.14 就不认 voltage.gpu。因此这里只保留确定可用的字段，
    /// 电压单独做一次探测后再决定是否加入。
    /// </summary>
    private static readonly string[] BaseFields =
    {
        "index", "name", "driver_version", "utilization.gpu",
        "clocks.current.graphics", "clocks.current.memory",
        "temperature.gpu", "power.draw", "power.limit", "power.max_limit",
        "fan.speed", "memory.total", "memory.used",
    };

    private static readonly string[] Fields = BuildFields();

    /// <summary>字段名 → 在 CSV 行里的列序号。这样 Fields 变动时解析不会错位。</summary>
    private static readonly Dictionary<string, int> FieldColumn =
        Fields.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);

    private static string[] BuildFields()
    {
        var list = BaseFields.ToList();
        if (SupportsField("voltage.gpu")) list.Add("voltage.gpu");
        return list.ToArray();
    }

    /// <summary>探测某个 nvidia-smi 字段在当前驱动上是否可查询。</summary>
    private static bool SupportsField(string field)
    {
        try
        {
            var smi = CollectUtil.FindExecutable("nvidia-smi.exe");
            if (smi is null) return false;
            var outp = CollectUtil.RunProcessUtf8(smi, $"--query-gpu={field} --format=csv,noheader,nounits -i 0", 4000, out var err);
            if (outp.Contains("not a valid field", StringComparison.OrdinalIgnoreCase)) return false;
            if (outp.Contains("not recognized", StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(err);
        }
        catch { return false; }
    }

    /// <summary>
    /// 把 nvidia-smi 的 CSV 输出切成"每张卡一个字典"。
    /// 输出形如：<c>0, NVIDIA GeForce RTX 4060 Laptop GPU, 617.14, 32, 285, ...</c>
    /// </summary>
    internal static List<Dictionary<string, string>> ParseCsvRows(string csv, out string error)
        => ParseCsvRows(csv, Fields, out error);

    /// <summary>
    /// 按**指定的字段列表**解析 nvidia-smi 的 CSV 输出。
    /// 调用方可以只查几个字段（例如机器信息里只查 name,driver_version,memory.total），
    /// 此时列数不是 <see cref="Fields"/>，必须传入对应的字段数组，否则所有行都会被当成坏行跳过。
    /// </summary>
    internal static List<Dictionary<string, string>> ParseCsvRows(string csv, string[] fields, out string error)
    {
        error = "";
        var rows = new List<Dictionary<string, string>>();
        var badLines = new List<string>();

        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();

            // 名字里如果含逗号会导致列数偏多，退一步按 ", " 重切
            if (parts.Length != fields.Length)
            {
                var alt = line.Split(", ").Select(p => p.Trim()).ToArray();
                if (alt.Length == fields.Length) parts = alt;
            }

            // 【关键】nvidia-smi 会把错误信息打到 stdout，必须跳过非数据行，
            // 否则会拿错误文本当数值，或者直接让整次解析失败。
            var firstCell = parts.Length > 0 ? parts[0] : "";
            bool firstIsNumber = int.TryParse(firstCell, out _);
            bool firstIsName = !firstIsNumber && fields.Length > 0
                               && fields[0].Equals("name", StringComparison.OrdinalIgnoreCase)
                               && !firstCell.StartsWith("[", StringComparison.Ordinal);
            if (parts.Length != fields.Length || (!firstIsNumber && !firstIsName))
            {
                badLines.Add(line);
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fields.Length; i++) row[fields[i]] = parts[i];
            rows.Add(row);
        }

        if (rows.Count == 0)
            error = badLines.Count > 0
                ? $"nvidia-smi 没有返回可用数据行，原始输出：{string.Join(" | ", badLines.Take(3))}"
                : "nvidia-smi 没有输出任何行";
        else if (badLines.Count > 0)
            error = $"已跳过 {badLines.Count} 行非数据输出：{string.Join(" | ", badLines.Take(2))}";

        return rows;
    }

    internal static List<GpuInfo> ParseGpus(string csv, out string error)
        => ParseGpus(csv, Fields, out error);

    internal static List<GpuInfo> ParseGpus(string csv, string[] fields, out string error)
    {
        var rows = ParseCsvRows(csv, fields, out error);
        var list = new List<GpuInfo>();
        foreach (var r in rows)
        {
            var name = Get(r, "name");
            if (string.IsNullOrWhiteSpace(name)) name = Get(r, "product_name");
            list.Add(new GpuInfo
            {
                Name = name,
                DriverVersion = Get(r, "driver_version"),
                VramMb = ParseNumber(Get(r, "memory.total")),
            });
        }
        return list;
    }

    private static string Get(Dictionary<string, string> row, string field)
        => row.TryGetValue(field, out var v) ? v : "";

    /// <summary>
    /// 把 nvidia-smi 的一个单元格转成数值。
    /// <c>nounits</c> 下是裸数字；拿不到的项是 <c>[N/A]</c> / <c>[Not Supported]</c>，返回 null。
    /// </summary>
    internal static double? ParseNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.StartsWith("[", StringComparison.Ordinal)) return null;   // [N/A] / [Not Supported]
        if (s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)) return null;

        // 容错：带单位时（如 "32 %" / "8188 MiB" / "6.71 W"）只取前导数字部分
        var cleaned = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch is '.' or '-' or '+' or 'e' or 'E').ToArray());
        if (cleaned.Length == 0) return null;
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? CollectUtil.Sanitize(v) : null;
    }

    public void Start(int targetProcessId, string targetProcessName)
    {
        _processId = targetProcessId;
        _processName = targetProcessName;
        _notes.Clear();
        Gpus.Clear();
        DisposeCounters();

        if (_nvidiaSmi is not null)
        {
            var csv = CollectUtil.RunProcessUtf8(_nvidiaSmi, GpuQueryArgs(0), 5000, out _);
            var parsed = ParseGpus(csv, out var perr);
            Gpus.AddRange(parsed);
            if (parsed.Count == 0 && !string.IsNullOrWhiteSpace(perr)) _notes.Add($"nvidia-smi：{perr}");
        }

        if (_elevated || true)
        {
            // 逐个实例筛选：只有属于目标进程的计数器才纳入
            var pidToken = $"pid_{_processId}_";
            try
            {
                var engCat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in engCat.GetInstanceNames())
                {
                    if (!inst.StartsWith(pidToken, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                    c.NextValue();
                    _gpuEngine3D.Add(c);
                }
            }
            catch (Exception ex) { _notes.Add($"GPU Engine 计数器不可用：{ex.Message}"); }

            try
            {
                var memCat = new PerformanceCounterCategory("GPU Process Memory");
                foreach (var inst in memCat.GetInstanceNames())
                {
                    if (!inst.StartsWith(pidToken, StringComparison.OrdinalIgnoreCase)) continue;
                    if (inst.Contains("Dedicated", StringComparison.OrdinalIgnoreCase))
                    {
                        var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, readOnly: true);
                        c.NextValue(); _procMemDedicated.Add(c);
                    }
                    else if (inst.Contains("Shared", StringComparison.OrdinalIgnoreCase))
                    {
                        var c = new PerformanceCounter("GPU Process Memory", "Shared Usage", inst, readOnly: true);
                        c.NextValue(); _procMemShared.Add(c);
                    }
                }
            }
            catch (Exception ex) { _notes.Add($"GPU Process Memory 计数器不可用：{ex.Message}"); }

            try
            {
                var adCat = new PerformanceCounterCategory("GPU Adapter Memory");
                foreach (var inst in adCat.GetInstanceNames())
                {
                    if (!inst.Contains("Dedicated", StringComparison.OrdinalIgnoreCase)) continue;
                    var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, readOnly: true);
                    c.NextValue(); _adapterMem.Add(c);
                }
            }
            catch (Exception ex) { _notes.Add($"GPU Adapter Memory 计数器不可用：{ex.Message}"); }

            if (_gpuEngine3D.Count == 0)
                _notes.Add("目标进程当前没有 3D 引擎活动实例（游戏未渲染或采样瞬间无 GPU 工作）。");
        }
    }

    public void Collect(PerformanceSample sample)
    {
        foreach (var n in _notes)
            if (!sample.Notes.Contains(n)) sample.Notes.Add(n);

        // ---- nvidia-smi 主数据 ----
        if (_nvidiaSmi is not null)
        {
            var csv = CollectUtil.RunProcessUtf8(_nvidiaSmi, GpuQueryArgs(0), 3000, out var err);
            if (string.IsNullOrWhiteSpace(csv))
            {
                sample.Notes.Add($"nvidia-smi 无输出：{err}");
            }
            else
            {
                var rows = ParseCsvRows(csv, out var perr);
                if (!string.IsNullOrWhiteSpace(perr)) sample.Notes.Add($"nvidia-smi：{perr}");

                var g = rows.FirstOrDefault();
                if (g is not null)
                {
                    sample.GpuName = Get(g, "name").Length > 0 ? Get(g, "name") : Get(g, "product_name");
                    sample.GpuUtilPercent = ParseNumber(Get(g, "utilization.gpu"));
                    sample.GpuClockMhz = ParseNumber(Get(g, "clocks.current.graphics"));
                    sample.GpuMemClockMhz = ParseNumber(Get(g, "clocks.current.memory"));
                    sample.GpuTempC = ParseNumber(Get(g, "temperature.gpu"));
                    sample.GpuVoltageMv = ParseNumber(Get(g, "voltage.gpu"));
                    sample.GpuPowerW = ParseNumber(Get(g, "power.draw"));
                    // 本机实测 power.limit 是 [N/A]，power.max_limit 才有值（100.00 W）
                    sample.GpuPowerLimitW = ParseNumber(Get(g, "power.limit")) ?? ParseNumber(Get(g, "power.max_limit"));
                    sample.GpuFanPercent = ParseNumber(Get(g, "fan.speed"));
                    sample.GpuTotalDedicatedMemoryMb = ParseNumber(Get(g, "memory.used"));
                    sample.GpuTotalDedicatedMemoryLimitMb = ParseNumber(Get(g, "memory.total"));

                    if (Gpus.Count == 0 && !string.IsNullOrWhiteSpace(sample.GpuName))
                        Gpus.Add(new GpuInfo
                        {
                            Name = sample.GpuName!,
                            DriverVersion = Get(g, "driver_version"),
                            VramMb = sample.GpuTotalDedicatedMemoryLimitMb,
                        });
                }
            }
        }

        // ---- 性能计数器：本进程显存 / 本进程 3D 占用 ----
        var dedicated = SumCounters(_procMemDedicated);
        var shared = SumCounters(_procMemShared);
        if (dedicated is not null) sample.GpuProcessDedicatedMemoryMb = Math.Round(dedicated.Value / 1024.0 / 1024.0, 0);
        if (shared is not null) sample.GpuProcessSharedMemoryMb = Math.Round(shared.Value / 1024.0 / 1024.0, 0);

        var adapter = SumCounters(_adapterMem);
        if (adapter is not null && sample.GpuTotalDedicatedMemoryMb is null)
            sample.GpuTotalDedicatedMemoryMb = Math.Round(adapter.Value / 1024.0 / 1024.0, 0);

        // nvidia-smi 拿不到占用时，用本进程 3D 引擎计数兜底
        if (sample.GpuUtilPercent is null && _gpuEngine3D.Count > 0)
        {
            var v = SumCounters(_gpuEngine3D);
            if (v is not null) sample.GpuUtilPercent = Math.Round(Math.Min(v.Value, 100.0), 1);
        }
    }

    private static double? SumCounters(List<PerformanceCounter> counters)
    {
        if (counters.Count == 0) return null;
        double total = 0;
        bool any = false;
        foreach (var c in counters)
        {
            try
            {
                var v = CollectUtil.Sanitize(c.NextValue());
                if (v is not null) { total += v.Value; any = true; }
            }
            catch { /* 单个实例失效不影响其它 */ }
        }
        return any ? total : null;
    }

    private void DisposeCounters()
    {
        foreach (var list in new[] { _gpuEngine3D, _procMemDedicated, _procMemShared, _adapterMem })
        {
            foreach (var c in list) { try { c.Dispose(); } catch { /* 忽略 */ } }
            list.Clear();
        }
    }

    public void Stop() => DisposeCounters();

    public void Dispose() => Stop();
}
