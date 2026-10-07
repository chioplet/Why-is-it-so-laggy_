using System.Diagnostics;

namespace Gpd.Core.Collect;

/// <summary>采样参数。</summary>
public sealed class SamplingOptions
{
    /// <summary>目标进程 Id。</summary>
    public int ProcessId { get; set; }
    /// <summary>目标进程名（不含 .exe）。</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>采样间隔（秒）。默认 1.0。</summary>
    public double IntervalSeconds { get; set; } = 1.0;
    /// <summary>定时模式的总时长（秒）。为 null 表示手动模式，直到用户点停止。</summary>
    public double? DurationSeconds { get; set; }
    /// <summary>目标进程退出后是否自动结束采样。</summary>
    public bool StopWhenProcessExits { get; set; } = true;
    /// <summary>是否启用帧率模块（可选）。关闭则完全不启动 PresentMon。</summary>
    public bool EnableFps { get; set; }
}

/// <summary>采样进度事件参数。</summary>
public sealed class SampleProgressEventArgs : EventArgs
{
    public PerformanceSample Sample { get; init; } = new();
    public int SampleIndex { get; init; }
    public double ElapsedSeconds { get; init; }
}

/// <summary>
/// 采样编排器：按固定间隔驱动各个采集器，聚合成 <see cref="SampleSession"/>。
/// </summary>
/// <remarks>
/// 设计约束：
///  1. 任何单个采集器抛异常都不能中断整段采样 —— 异常会被记录到 <see cref="PerformanceSample.Notes"/> 后继续。
///  2. 采样循环跑在后台任务上，界面线程通过 <see cref="ProgressChanged"/> 拿数据（事件在后台线程触发）。
///  3. 停止原因必须如实记录：Manual / Duration / ProcessExited / Error。
/// </remarks>
public sealed class PerformanceSampler : IDisposable
{
    private readonly List<IPerformanceCollector> _collectors = new();
    private readonly List<CollectorStatus> _statuses = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Stopwatch _clock = new();
    private readonly object _stopReasonGate = new();
    private string _stopReason = "Manual";
    private System.Timers.Timer? _durationTimer;
    private SampleSession? _session;

    /// <summary>每采集完一个时间点触发一次（在后台线程）。</summary>
    public event EventHandler<SampleProgressEventArgs>? ProgressChanged;
    /// <summary>采样结束时触发（在后台线程）。</summary>
    public event EventHandler<SampleSession>? Completed;

    /// <summary>各采集器的可用性探测结果。</summary>
    public IReadOnlyList<CollectorStatus> CollectorStatuses => _statuses;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>当前累积的会话（采样中也可读，用于界面实时展示）。</summary>
    public SampleSession? CurrentSession => _session;

    /// <summary>
    /// 构造采样器。会为每个采集器调用一次 <see cref="IPerformanceCollector.Probe"/>（不启动采集）。
    /// </summary>
    public PerformanceSampler(bool enableFps = false)
    {
        var cpu = new CpuCollector();
        var gpu = new GpuCollector();
        var mem = new MemoryCollector();
        _collectors.Add(cpu);
        _collectors.Add(gpu);
        _collectors.Add(mem);
        if (enableFps)
            _collectors.Add(new PresentMonFpsCollector());

        // CPU 标称频率用于把 % Processor Performance 换算成 MHz。
        // 【顺序很重要】必须在 Probe() 之前注入，否则 Probe 的说明文字里会出现"标称 0MHz"。
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                cpu.NominalMhz = Convert.ToDouble(o["MaxClockSpeed"] ?? 0);
                break;
            }
        }
        catch { /* 拿不到就不换算，报告里 CPUFrequencyMhz 会留空（有 Processor Frequency 时仍可用） */ }

        foreach (var c in _collectors)
        {
            try { _statuses.Add(c.Probe()); }
            catch (Exception ex) { _statuses.Add(new CollectorStatus { Name = c.Name, Available = false, Detail = ex.Message }); }
        }
    }

    /// <summary>开始采样。立即返回，采样在后台进行。</summary>
    public void Start(SamplingOptions options)
    {
        if (IsRunning) throw new InvalidOperationException("采样已在进行中。");

        _cts = new CancellationTokenSource();
        _clock.Restart();
        lock (_stopReasonGate) _stopReason = "Manual";

        _session = new SampleSession
        {
            ProcessId = options.ProcessId,
            ProcessName = options.ProcessName,
            IntervalSeconds = options.IntervalSeconds,
            StartedAt = DateTimeOffset.Now,
        };

        try
        {
            using var p = Process.GetProcessById(options.ProcessId);
            _session.ProcessPath = SafeMainModulePath(p);
        }
        catch { /* 拿不到路径不影响采样 */ }

        _session.Machine = MachineInfoCollector.Collect(_statuses
            .Select(s => new CapabilityProbe { Name = s.Name, Available = s.Available, Detail = s.Detail })
            .ToList());
        foreach (var s in _statuses.Where(s => !s.Available))
            _session.Warnings.Add($"{s.Name} 不可用：{s.Detail}");

        foreach (var c in _collectors)
        {
            try { c.Start(options.ProcessId, options.ProcessName); }
            catch (Exception ex) { _session.Warnings.Add($"{c.Name} 启动失败：{ex.Message}"); }
        }

        if (options.DurationSeconds is > 0)
        {
            _durationTimer = new System.Timers.Timer(options.DurationSeconds.Value * 1000);
            _durationTimer.Elapsed += (_, _) =>
            {
                lock (_stopReasonGate) _stopReason = "Duration";
                _cts?.Cancel();
            };
            _durationTimer.AutoReset = false;
            _durationTimer.Start();
        }

        _loop = Task.Run(() => Loop(options, _cts.Token));
    }

    private async Task Loop(SamplingOptions options, CancellationToken token)
    {
        var session = _session!;
        int index = 0;
        var interval = TimeSpan.FromSeconds(Math.Max(options.IntervalSeconds, 0.2));
        try
        {
            // 【实测教训】这里原先先 await Task.Delay(一个采样间隔) 再进循环，结果定时模式
            // （DurationSeconds=12）在前 1 秒的 Delay 里就被定时器 Cancel 掉，样本数恒为 0。
            // 正确做法：只用一段短预热（让需要差值的计数器拿到基线），随即采第一个样本，
            // 把间隔等待挪到每轮循环的**末尾**。
            await Task.Delay(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                var sample = new PerformanceSample
                {
                    ElapsedSeconds = Math.Round(_clock.Elapsed.TotalSeconds, 2),
                    Timestamp = DateTimeOffset.Now,
                };

                foreach (var c in _collectors)
                {
                    try { c.Collect(sample); }
                    catch (Exception ex) { sample.Notes.Add($"{c.Name} 采集异常：{ex.Message}"); }
                }

                lock (_gate) session.Samples.Add(sample);
                SafeRaiseProgress(new SampleProgressEventArgs
                {
                    Sample = sample,
                    SampleIndex = index,
                    ElapsedSeconds = sample.ElapsedSeconds,
                });
                index++;

                // 目标进程退出检测
                if (options.StopWhenProcessExits && !IsProcessAlive(options.ProcessId))
                {
                    lock (_stopReasonGate)
                        if (_stopReason == "Manual") _stopReason = "ProcessExited";
                    session.Warnings.Add("目标进程已退出，采样自动结束。");
                    break;
                }

                try
                {
                    // 【实测教训】这里曾经写成 TimeSpan.FromSeconds(interval * 1000)，
                    // 于是 1 秒的间隔变成了 1000 秒 —— 15 秒采样只出 1 个样本。
                    // FromSeconds 收的就是秒，不要再乘 1000。
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.IntervalSeconds, 0.05)), token)
                              .ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户停止或定时到点，属正常路径
        }
        catch (Exception ex)
        {
            lock (_stopReasonGate) _stopReason = "Error";
            session.Warnings.Add($"采样循环异常终止：{ex}");
        }
        finally
        {
            foreach (var c in _collectors)
            {
                try { c.Stop(); } catch { /* 忽略 */ }
            }
            _clock.Stop();
            session.EndedAt = DateTimeOffset.Now;
            lock (_stopReasonGate) session.StopReason = _stopReason;
            try { _durationTimer?.Dispose(); } catch { /* 忽略 */ }
            _durationTimer = null;
            SafeRaiseCompleted(session);
        }
    }

    private void SafeRaiseProgress(SampleProgressEventArgs e)
    {
        try { ProgressChanged?.Invoke(this, e); }
        catch { /* 订阅方异常不能影响采样 */ }
    }

    private void SafeRaiseCompleted(SampleSession s)
    {
        try { Completed?.Invoke(this, s); }
        catch { /* 同上 */ }
    }

    /// <summary>请求停止采样（手动模式）。</summary>
    public void Stop()
    {
        lock (_stopReasonGate)
            if (_stopReason == "Manual") _stopReason = "Manual";
        _cts?.Cancel();
    }

    /// <summary>等待采样循环结束。</summary>
    public async Task WaitForCompletionAsync()
    {
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* 异常已在循环内处理 */ }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static string SafeMainModulePath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        foreach (var c in _collectors)
        {
            try { c.Dispose(); } catch { /* 忽略 */ }
        }
        try { _cts?.Dispose(); } catch { /* 忽略 */ }
    }
}
