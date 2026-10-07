using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gpd.Core.Collect;

/// <summary>
/// 帧率采集器（可选模块）。用 PresentMon 采集逐帧数据，再折算成 1Hz 的 FPS / 帧时间 / 1% Low / 0.1% Low。
/// </summary>
/// <remarks>
/// 【重要限制，已实测】PresentMon 启动 ETW 内核会话需要管理员权限。
/// 非管理员下它不会报错，而是静默退出并且不产生 CSV —— 本类会识别这种情况，
/// 把原因写进 <see cref="PerformanceSample.Notes"/>，并且 <see cref="Probe"/> 直接报不可用。
/// 因此上层（界面/报告）必须把"没有帧率数据"当成正常降级，而不是错误。
/// </remarks>
public sealed class PresentMonFpsCollector : IFpsCollector
{
    public string Name => "帧率（PresentMon 逐帧采集）";
    public bool RequiresElevation => true;

    private readonly List<string> _notes = new();
    private Process? _pm;
    private string _csvPath = "";
    private string? _exePath;
    private int _processId;
    private string _processName = "";

    private readonly object _gate = new();
    private long _csvOffset;
    private readonly List<double> _recentFrameTimes = new();   // 最近窗口内的帧时间（ms）
    private double _lastFrameTimestamp;
    private int _totalFrames;
    private DateTime _lastProgress = DateTime.MinValue;
    private string _lastParseError = "";
    private Thread? _reader;
    private volatile bool _running;

    /// <summary>参与统计的最近帧数上限（约 10 秒 @1000fps 的余量）。</summary>
    private const int MaxRecentFrames = 20000;

    /// <summary>写入的 CSV 位置（供报告引用）。</summary>
    public string CsvPath => _csvPath;

    public CollectorStatus Probe()
    {
        var status = new CollectorStatus { Name = Name };
        _exePath = FindPresentMon();
        if (_exePath is null)
        {
            status.Available = false;
            status.Detail = "找不到 PresentMon_x64.exe。" +
                            "可安装 NVIDIA FrameView SDK、Intel PresentMon 或 CapFrameX 后重试；" +
                            "在找到之前，本应用只提供 1Hz 的系统级指标，不含帧率。";
            return status;
        }

        if (!CollectUtil.IsElevated())
        {
            status.Available = false;
            status.Detail = $"已找到 {_exePath}，但当前进程不是管理员。" +
                            "PresentMon 需要管理员权限才能启动 ETW 内核会话（实测非管理员会静默退出、不产生数据）。" +
                            "请以管理员身份重新运行本程序。";
            return status;
        }

        status.Available = true;
        status.Detail = $"{_exePath}（管理员权限已就绪），将按进程过滤逐帧采集";
        return status;
    }

    /// <summary>
    /// 依次在常见位置查找 PresentMon。返回 null 表示没找到。
    /// </summary>
    public static string? FindPresentMon()
    {
        var found = CollectUtil.FindExecutable("PresentMon_x64.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "FrameViewSDK", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Intel", "PresentMon"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CapFrameX"));
        if (found is not null) return found;
        return CollectUtil.FindExecutable("PresentMon.exe");
    }

    public void Start(int targetProcessId, string targetProcessName)
    {
        _processId = targetProcessId;
        _processName = targetProcessName;
        _notes.Clear();
        _csvOffset = 0;
        _totalFrames = 0;
        _lastFrameTimestamp = 0;
        lock (_gate) _recentFrameTimes.Clear();

        if (_exePath is null) _exePath = FindPresentMon();
        if (_exePath is null)
        {
            _notes.Add("未找到 PresentMon_x64.exe，本次采样不含帧率数据。");
            return;
        }
        if (!CollectUtil.IsElevated())
        {
            _notes.Add("非管理员运行，PresentMon 无法启动 ETW 会话，本次采样不含帧率数据（这是权限限制，不是游戏问题）。");
            return;
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "GpdPresentMon");
            Directory.CreateDirectory(dir);
            _csvPath = Path.Combine(dir, $"frames_{targetProcessName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            if (File.Exists(_csvPath)) File.Delete(_csvPath);

            var args = $"--output_file \"{_csvPath}\" --process_id {targetProcessId} " +
                       "--terminate_on_proc_exit --stop_existing_session --no_console_stats";
            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            _pm = Process.Start(psi);
            if (_pm is null)
            {
                _notes.Add("PresentMon 进程启动失败。");
                return;
            }

            _running = true;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "GpdPresentMonReader" };
            _reader.Start();
            _notes.Add($"PresentMon 已启动（PID {_pm.Id}，过滤目标进程 {targetProcessId}），CSV: {_csvPath}");
        }
        catch (Exception ex)
        {
            _notes.Add($"PresentMon 启动异常：{ex.Message}");
        }
    }

    /// <summary>后台读取 CSV 增量行。PresentMon 边采边写，必须增量读、不能一次读全。</summary>
    private void ReaderLoop()
    {
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool headerParsed = false;
        var stall = Stopwatch.StartNew();

        while (_running)
        {
            try
            {
                if (!File.Exists(_csvPath))
                {
                    Thread.Sleep(200);
                    // 启动 6 秒后还是没文件，说明 PresentMon 起不来（多为权限/会话冲突）
                    if (stall.Elapsed.TotalSeconds > 6 && _pm is { HasExited: true })
                    {
                        _notes.Add($"PresentMon 已退出（退出码 {_pm.ExitCode}）且没有产生 CSV —— " +
                                   "通常是 ETW 会话权限不足或已有另一个采集会话在运行。");
                        _running = false;
                    }
                    continue;
                }

                using var fs = new FileStream(_csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= _csvOffset)
                {
                    Thread.Sleep(150);
                    continue;
                }
                fs.Seek(_csvOffset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);

                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    if (!headerParsed)
                    {
                        headerParsed = ParseHeader(line, headerMap);
                        if (!headerParsed) { _lastParseError = "PresentMon CSV 表头无法识别，已放弃帧率采集"; }
                        continue;
                    }
                    ParseFrameLine(line, headerMap);
                }
                _csvOffset = fs.Position;
            }
            catch (IOException)
            {
                Thread.Sleep(200);   // 文件被占用，稍后再试
            }
            catch (Exception ex)
            {
                _lastParseError = ex.Message;
                Thread.Sleep(300);
            }
        }
    }

    private static bool ParseHeader(string line, Dictionary<string, int> map)
    {
        var cols = SplitCsv(line);
        if (cols.Length < 3) return false;
        for (int i = 0; i < cols.Length; i++) map[cols[i].Trim()] = i;
        // PresentMon 2.x 用 "CPUStartTime"/"MsBetweenPresents"，老版本用 "TimeInSeconds"/"msBetweenPresents"
        return map.ContainsKey("MsBetweenPresents") || map.ContainsKey("msBetweenPresents");
    }

    private void ParseFrameLine(string line, Dictionary<string, int> map)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var cols = SplitCsv(line);
        if (cols.Length < 2) return;

        string? Get(params string[] names)
        {
            foreach (var n in names)
                if (map.TryGetValue(n, out var idx) && idx < cols.Length) return cols[idx];
            return null;
        }

        // 归属校验：只统计目标进程/交换链的帧
        var pidStr = Get("ProcessID", "Application");
        if (pidStr is not null && int.TryParse(pidStr, out var pid) && pid != _processId && pid != 0)
            return;

        var msStr = Get("MsBetweenPresents", "msBetweenPresents");
        if (msStr is null || !double.TryParse(msStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 10000)
            return;

        double ts = 0;
        var tsStr = Get("CPUStartTime", "TimeInSeconds", "TimeInMs", "QPCTime");
        if (tsStr is not null) double.TryParse(tsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out ts);

        lock (_gate)
        {
            _recentFrameTimes.Add(ms);
            if (_recentFrameTimes.Count > MaxRecentFrames)
                _recentFrameTimes.RemoveRange(0, _recentFrameTimes.Count - MaxRecentFrames);
            _lastFrameTimestamp = ts;
            _totalFrames++;
        }
    }

    private static string[] SplitCsv(string line)
    {
        // PresentMon 的字段名不含逗号，值也不会含逗号（进程名可能在 Application 列里含空格），简单切分即可
        return line.Split(',');
    }

    public void Collect(PerformanceSample sample)
    {
        foreach (var n in _notes)
            if (!sample.Notes.Contains(n)) sample.Notes.Add(n);

        if (!string.IsNullOrEmpty(_lastParseError) && !sample.Notes.Contains(_lastParseError))
            sample.Notes.Add(_lastParseError);

        double[] frames;
        int total;
        lock (_gate)
        {
            frames = _recentFrameTimes.ToArray();
            total = _totalFrames;
        }

        if (frames.Length < 5)
        {
            sample.FpsSource = null;
            sample.Notes.Add(frames.Length == 0
                ? "本秒没有采到帧（PresentMon 未运行或游戏未在渲染）"
                : $"本秒只采到 {frames.Length} 帧，数据不足");
            return;
        }

        sample.FpsSource = "PresentMon";

        // 用最近 1 秒左右（取最后 N 帧，N 由平均帧时间反推）的窗口算瞬时 FPS
        double avgMs = frames.Average();
        int window = Math.Clamp((int)Math.Round(1000.0 / Math.Max(avgMs, 0.1)), 5, frames.Length);
        var recent = frames.Skip(Math.Max(0, frames.Length - Math.Max(window, 1))).ToArray();

        double avgFrameMs = recent.Average();
        sample.FrameTimeMs = Math.Round(avgFrameMs, 2);
        sample.Fps = Math.Round(1000.0 / avgFrameMs, 1);

        // 整段（本次采样开始至今）的平均与 Low 帧
        sample.FpsAverage = Math.Round(1000.0 / avgMs, 1);
        var sorted = frames.OrderBy(v => v).ToArray();
        sample.Fps1PercentLow = Math.Round(1000.0 / Percentile(sorted, 0.99), 1);
        sample.Fps01PercentLow = Math.Round(1000.0 / Percentile(sorted, 0.999), 1);

        sample.Notes.Add($"累计采集 {total} 帧");
    }

    /// <summary>升序数组的 p 分位（p=0.99 表示取第 99% 位置的较大帧时间 → 1% Low）。</summary>
    private static double Percentile(double[] ascending, double p)
    {
        if (ascending.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p * ascending.Length) - 1;
        idx = Math.Clamp(idx, 0, ascending.Length - 1);
        var v = ascending[idx];
        return v <= 0 ? 1 : v;
    }

    public void Stop()
    {
        _running = false;
        try { _reader?.Join(1500); } catch { /* 忽略 */ }

        if (_pm is not null)
        {
            try
            {
                if (!_pm.HasExited) _pm.Kill(entireProcessTree: true);
            }
            catch { /* 忽略 */ }
            try { _pm.Dispose(); } catch { /* 忽略 */ }
            _pm = null;
        }
        _reader = null;
    }

    public void Dispose() => Stop();
}
