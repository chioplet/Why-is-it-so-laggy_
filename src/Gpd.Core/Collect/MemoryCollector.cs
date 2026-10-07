using System.Diagnostics;

namespace Gpd.Core.Collect;

/// <summary>
/// 内存 / 页面文件 / 磁盘采集器。数据来自性能计数器与进程 API。
/// </summary>
/// <remarks>
/// 实测可用计数器（中文 Windows 上英文名有效）：
///   \Memory\Available MBytes
///   \Memory\% Committed Bytes In Use
///   \Paging File(_Total)\% Usage
///   \PhysicalDisk(_Total)\% Disk Time
///   \PhysicalDisk(_Total)\Avg. Disk Queue Length
/// </remarks>
public sealed class MemoryCollector : IPerformanceCollector
{
    public string Name => "内存 / 页面文件 / 磁盘（性能计数器）";

    private PerformanceCounter? _availMb;
    private PerformanceCounter? _committed;
    private PerformanceCounter? _pageFile;
    private PerformanceCounter? _diskTime;
    private PerformanceCounter? _diskQueue;
    private PerformanceCounter? _procWorkingSet;   // 逐进程工作集计数器（可选）
    private readonly List<string> _notes = new();
    private int _processId;

    public CollectorStatus Probe()
    {
        var status = new CollectorStatus { Name = Name };
        var ok = new List<string>();
        var bad = new List<string>();

        void Check(string cat, string counter, string inst, string label)
        {
            try
            {
                using var c = new PerformanceCounter(cat, counter, inst, readOnly: true);
                c.NextValue();
                ok.Add(label);
            }
            catch (Exception ex) { bad.Add($"{label}（{ex.Message}）"); }
        }

        Check("Memory", "Available MBytes", "", "可用物理内存");
        Check("Memory", "% Committed Bytes In Use", "", "提交内存占用");
        Check("Paging File", "% Usage", "_Total", "页面文件占用");
        Check("PhysicalDisk", "% Disk Time", "_Total", "磁盘占用");
        Check("PhysicalDisk", "Avg. Disk Queue Length", "_Total", "磁盘队列");

        status.Available = ok.Count > 0;
        var d = new List<string>();
        if (ok.Count > 0) d.Add("可用：" + string.Join("、", ok));
        if (bad.Count > 0) d.Add("不可用：" + string.Join("、", bad));
        status.Detail = string.Join("；", d);
        return status;
    }

    public void Start(int targetProcessId, string targetProcessName)
    {
        _processId = targetProcessId;
        _notes.Clear();
        _availMb = TryCreate("Memory", "Available MBytes", "", "可用物理内存");
        _committed = TryCreate("Memory", "% Committed Bytes In Use", "", "提交内存占用");
        _pageFile = TryCreate("Paging File", "% Usage", "_Total", "页面文件占用");
        _diskTime = TryCreate("PhysicalDisk", "% Disk Time", "_Total", "磁盘占用");
        _diskQueue = TryCreate("PhysicalDisk", "Avg. Disk Queue Length", "_Total", "磁盘队列");

        // 进程工作集优先用 Process API（更准），计数器作为兜底
        try
        {
            var inst = FindProcessInstance(_processId);
            if (inst is not null)
                _procWorkingSet = TryCreate("Process", "Working Set", inst, "进程工作集");
        }
        catch { /* 忽略 */ }

        foreach (var c in new[] { _availMb, _committed, _pageFile, _diskTime, _diskQueue, _procWorkingSet })
        {
            try { c?.NextValue(); } catch { /* 预热失败忽略 */ }
        }
    }

    private string? FindProcessInstance(int pid)
    {
        try
        {
            var cat = new PerformanceCounterCategory("Process");
            var names = cat.GetInstanceNames();
            var baseName = "";
            try { using var p = Process.GetProcessById(pid); baseName = p.ProcessName; } catch { return null; }

            // 同名多实例时 Windows 用 name / name#1 / name#2 区分，需要按 ID Process 匹配
            foreach (var n in names.Where(n => n.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                                               n.StartsWith(baseName + "#", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var idc = new PerformanceCounter("Process", "ID Process", n, readOnly: true);
                    if ((int)idc.NextValue() == pid) return n;
                }
                catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    private PerformanceCounter? TryCreate(string cat, string counter, string inst, string label)
    {
        try { return new PerformanceCounter(cat, counter, inst, readOnly: true); }
        catch (Exception ex) { _notes.Add($"{label} 采集不可用：{ex.Message}"); return null; }
    }

    public void Collect(PerformanceSample sample)
    {
        foreach (var n in _notes)
            if (!sample.Notes.Contains(n)) sample.Notes.Add(n);

        sample.SystemAvailableMemoryMb = Read(_availMb, 0);
        sample.SystemCommittedPercent = Read(_committed, 1);
        sample.PageFilePercent = Read(_pageFile, 1);
        sample.DiskPercent = Read(_diskTime, 1);
        sample.DiskQueueLength = Read(_diskQueue, 2);

        try
        {
            using var p = Process.GetProcessById(_processId);
            p.Refresh();
            sample.ProcessWorkingSetMb = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 0);
            sample.ProcessPrivateMemoryMb = Math.Round(p.PrivateMemorySize64 / 1024.0 / 1024.0, 0);
        }
        catch (Exception ex)
        {
            // Process API 失败时退回性能计数器
            var ws = Read(_procWorkingSet, 0);
            if (ws is not null) sample.ProcessWorkingSetMb = Math.Round(ws.Value / 1024.0 / 1024.0, 0);
            sample.Notes.Add($"目标进程内存读取失败（{ex.Message}），已尝试用性能计数器兜底");
        }
    }

    private static double? Read(PerformanceCounter? c, int digits)
    {
        if (c is null) return null;
        try
        {
            var v = CollectUtil.Sanitize(c.NextValue());
            return v is null ? null : Math.Round(v.Value, digits);
        }
        catch { return null; }
    }

    public void Stop()
    {
        foreach (var c in new[] { _availMb, _committed, _pageFile, _diskTime, _diskQueue, _procWorkingSet })
        {
            try { c?.Dispose(); } catch { /* 忽略 */ }
        }
        _availMb = _committed = _pageFile = _diskTime = _diskQueue = _procWorkingSet = null;
    }

    public void Dispose() => Stop();
}
