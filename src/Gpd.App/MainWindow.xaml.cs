using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gpd.App.Reporting;
using Gpd.Core;
using Gpd.Core.Analysis;
using Gpd.Core.Collect;
using Gpd.Core.Games;
using Gpd.Core.Reporting;

namespace Gpd.App;

/// <summary>
/// 主窗口：选目标进程/游戏 → 采样 → 出报告。
/// </summary>
/// <remarks>
/// 三条线程纪律，改动时务必守住：
///  1. <see cref="PerformanceSampler"/> 的事件在后台线程触发，任何碰控件的地方都必须 <c>Dispatcher.BeginInvoke</c>。
///  2. 环境探测（性能计数器 Probe + nvidia-smi）要几百毫秒，必须在后台线程做，否则界面会卡白。
///  3. 采样器只能"先停旧、再建新"，不要复用：采集器持有句柄，复用容易把上次的计数器带进来。
/// </remarks>
public partial class MainWindow : Window
{
    // ── 目标选择 ────────────────────────────────────────────────────────────
    private readonly List<ProcessEntry> _allProcesses = new();
    private int? _targetPid;
    private string _targetName = "";
    private GameInfo? _selectedGame;

    // ── 采样 ────────────────────────────────────────────────────────────────
    private PerformanceSampler? _sampler;
    private SampleSession? _session;
    private bool _closing;

    // ── 实时读数与曲线 ──────────────────────────────────────────────────────
    private readonly Dictionary<string, TextBlock> _readouts = new();
    private readonly List<double> _chartX = new();
    private readonly List<double> _chartCpu = new();
    private readonly List<double> _chartGpu = new();

    private static readonly string[] ReadoutKeys =
    [
        "CPU 占用", "CPU 频率", "CPU 温度", "CPU 功耗",
        "GPU 占用", "GPU 频率", "GPU 温度", "GPU 功耗",
        "进程内存", "系统可用内存", "磁盘占用", "页面文件",
    ];

    public MainWindow()
    {
        InitializeComponent();
    }

    // ========================================================================
    //  生命周期
    // ========================================================================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildReadouts();
        InitChart();
        TxtOutputDir.Text = DefaultOutputDirectory();
        LoadProcesses();
        await ProbeEnvironmentAsync();
        Log("就绪。选择一个进程或游戏，设置采样参数后点「开始采样」。");
        Log("提示：帧率模块（PresentMon）需要管理员权限；本程序若未提权，会自动降级而不是报错。");
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        try
        {
            if (_sampler is { IsRunning: true })
            {
                var keep = MessageBox.Show(
                    "采样还在进行中，退出会丢失未生成报告的这段数据。确定退出？",
                    "wiisl", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (keep != MessageBoxResult.OK)
                {
                    e.Cancel = true;
                    _closing = false;
                    return;
                }
                _sampler.Stop();
            }
            _sampler?.Dispose();
        }
        catch { /* 退出路径不抛异常 */ }
    }

    // ========================================================================
    //  环境探测
    // ========================================================================

    private async Task ProbeEnvironmentAsync()
    {
        BtnProbe.IsEnabled = false;
        TxtElevation.Text = "权限：检测中…";
        try
        {
            var result = await Task.Run(() =>
            {
                using var probe = new PerformanceSampler(enableFps: false);
                var statuses = probe.CollectorStatuses.ToList();
                var machine = MachineInfoCollector.Collect(statuses
                    .Select(s => new CapabilityProbe { Name = s.Name, Available = s.Available, Detail = s.Detail })
                    .ToList());
                return (Statuses: statuses, Machine: machine, Elevated: CollectUtil.IsElevated());
            });

            TxtMachine.Text = DescribeMachine(result.Machine);

            if (result.Elevated)
            {
                TxtElevation.Text = "权限：管理员（帧率模块可用）";
                TxtElevation.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
            }
            else
            {
                TxtElevation.Text = "权限：普通用户（无法采集帧率；GPU 功耗/电压可能缺失）";
                TxtElevation.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            }

            Log("── 采集能力探测 ──");
            foreach (var s in result.Statuses)
                Log($"[{(s.Available ? "可用" : "不可用")}] {s.Name}：{s.Detail}");

            if (result.Machine.Capabilities.Count > 0 && result.Statuses.Count == 0)
                Log("（机器信息未附带能力列表）");
        }
        catch (Exception ex)
        {
            TxtElevation.Text = "权限：检测失败";
            Log($"环境探测失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            BtnProbe.IsEnabled = true;
        }
    }

    private async void BtnProbe_Click(object sender, RoutedEventArgs e) => await ProbeEnvironmentAsync();

    /// <summary>
    /// 只读采集里唯一需要管理员的是帧率模块（PresentMon 要开 ETW 内核会话）。
    /// 所以这里不强制提权，而是给一个"要用帧率就自己点"的按钮。
    /// </summary>
    private void BtnRestartElevated_Click(object sender, RoutedEventArgs e)
    {
        if (CollectUtil.IsElevated())
        {
            MessageBox.Show("当前已经是管理员权限，不需要重新启动。", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            Log("找不到当前程序路径，无法以管理员身份重启。请手动右键 exe → 以管理员身份运行。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",   // 触发 UAC / 凭据提示
            });
            Log("已请求以管理员身份重新启动，本窗口即将关闭。");
            _closing = true;
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户在 UAC 提示里点了"否"，或本机没有可用的管理员凭据
            Log("提权被取消（或本机账户没有管理员凭据）。程序继续以普通权限运行，帧率模块保持降级状态。");
        }
        catch (Exception ex)
        {
            Log($"以管理员身份重启失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string DescribeMachine(MachineInfo m)
    {
        var gpus = m.Gpus.Count == 0
            ? "未识别到显卡"
            : string.Join("；", m.Gpus.Select(g =>
                $"{g.Name}{(g.VramMb.HasValue ? $" {g.VramMb.Value / 1024.0:F0}GB" : "")}"
                + (string.IsNullOrEmpty(g.DriverVersion) ? "" : $" 驱动 {g.DriverVersion}")
                + (g.IsVirtualDisplayAdapter ? "（虚拟显示器）" : "")));

        return $"系统：{m.OsName} {m.OsVersion}（build {m.OsBuild}）\n"
             + $"CPU：{m.CpuName}，{m.CpuPhysicalCores} 物理核 / {m.CpuLogicalProcessors} 逻辑处理器，标称 {m.CpuNominalMhz:F0} MHz\n"
             + $"内存：{m.TotalMemoryMb:F0} MB\n"
             + $"显卡：{gpus}\n"
             + $"电源计划：{(string.IsNullOrWhiteSpace(m.PowerPlanName) ? "读取失败" : m.PowerPlanName)}"
             + $"　游戏模式：{BoolText(m.GameModeEnabled)}"
             + $"　硬件加速 GPU 计划：{BoolText(m.HardwareGpuSchedulingEnabled)}";
    }

    private static string BoolText(bool? v) => v switch { true => "开", false => "关", _ => "未知" };

    // ========================================================================
    //  进程列表
    // ========================================================================

    private sealed class ProcessEntry
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Title { get; init; } = "";
        public string Display => string.IsNullOrWhiteSpace(Title)
            ? $"{Name}  (PID {Id})"
            : $"{Name}  (PID {Id}) — {Title}";
    }

    private void LoadProcesses()
    {
        _allProcesses.Clear();
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!seen.Add(p.Id)) continue;
                var name = p.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                string title = "";
                try
                {
                    // 未提权时读取其它进程的 MainWindowTitle 会抛异常，属正常情况
                    if (p.MainWindowHandle != IntPtr.Zero) title = p.MainWindowTitle ?? "";
                }
                catch { /* 忽略：拿不到标题不影响选择 */ }

                _allProcesses.Add(new ProcessEntry { Id = p.Id, Name = name, Title = title });
            }
            catch { /* 进程可能已退出 */ }
            finally { p.Dispose(); }
        }

        _allProcesses.Sort((a, b) =>
        {
            // 有窗口的排前面：游戏一定有窗口，这样用户一眼就能找到
            var aWin = a.Title.Length > 0 ? 0 : 1;
            var bWin = b.Title.Length > 0 ? 0 : 1;
            var c = aWin.CompareTo(bWin);
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });

        ApplyProcessFilter();
    }

    private void ApplyProcessFilter()
    {
        var filter = TxtProcessFilter.Text.Trim();
        IEnumerable<ProcessEntry> view = _allProcesses;
        if (filter.Length > 0)
        {
            view = view.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Id.ToString(CultureInfo.InvariantCulture) == filter);
        }

        var list = view.ToList();
        LstProcesses.ItemsSource = list;
        TxtProcessCount.Text = filter.Length > 0
            ? $"匹配 {list.Count} / 共 {_allProcesses.Count} 个进程"
            : $"共 {_allProcesses.Count} 个进程（有窗口的排在前面）";
    }

    private void BtnRefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        LoadProcesses();
        Log($"已刷新进程列表，共 {_allProcesses.Count} 个。");
    }

    private void TxtProcessFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();

    private void LstProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstProcesses.SelectedItem is not ProcessEntry entry) return;
        _selectedGame = null;   // 手动选进程就不再关联游戏库
        SetTarget(entry.Id, entry.Name, null);
    }

    // ========================================================================
    //  游戏库
    // ========================================================================

    private sealed class GameEntry
    {
        public GameInfo Game { get; init; } = new();
        public string Title => Game.Name;
        public string Subtitle { get; init; } = "";
    }

    private async void BtnScanGames_Click(object sender, RoutedEventArgs e)
    {
        BtnScanGames.IsEnabled = false;
        TxtGameCount.Text = "正在扫描游戏库…";
        var includeStandalone = ChkStandalone.IsChecked == true;

        try
        {
            var warnings = new List<string>();
            var games = await Task.Run(() =>
                GameLibraryService.ScanAll(warnings, includeStandalone, attachConfigFiles: true));

            var entries = games
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GameEntry
                {
                    Game = g,
                    Subtitle = string.Join(" · ", new[]
                    {
                        g.PlatformName,
                        g.ConfigFiles.Count > 0 ? $"{g.ConfigFiles.Count} 个配置文件" : "未找到配置文件",
                        string.IsNullOrWhiteSpace(g.InstallDir) ? "" : Shorten(g.InstallDir, 70),
                        string.IsNullOrWhiteSpace(g.DiscoverySource) ? "" : g.DiscoverySource,
                    }.Where(s => s.Length > 0)),
                })
                .ToList();

            LstGames.ItemsSource = entries;
            var withConfig = entries.Count(x => x.Game.ConfigFiles.Count > 0);
            TxtGameCount.Text = $"识别到 {entries.Count} 个游戏，其中 {withConfig} 个找到了配置文件"
                              + (includeStandalone ? "（含独立安装扫描）" : "");

            Log($"── 游戏库扫描 ── 识别到 {entries.Count} 个游戏（{withConfig} 个含配置文件）");
            foreach (var w in warnings.Distinct().Take(20)) Log($"  扫描说明：{w}");
            if (warnings.Count > 20) Log($"  （另有 {warnings.Count - 20} 条扫描说明未显示）");
        }
        catch (Exception ex)
        {
            TxtGameCount.Text = "扫描失败。";
            Log($"游戏库扫描失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            BtnScanGames.IsEnabled = true;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void LstGames_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstGames.SelectedItem is not GameEntry entry) return;

        _selectedGame = entry.Game;
        var pid = FindRunningProcess(entry.Game.ProcessName);

        if (pid.HasValue)
        {
            SetTarget(pid.Value, entry.Game.ProcessName.Length > 0 ? entry.Game.ProcessName : entry.Game.Name, entry.Game);
        }
        else
        {
            _targetPid = null;
            _targetName = "";
            TxtTarget.Text = $"已选择游戏「{entry.Game.Name}」，但它当前没有在运行。";
            TxtTargetGame.Text = string.IsNullOrWhiteSpace(entry.Game.ProcessName)
                ? $"未识别到主程序名，请从「任意进程」页手动选择运行的进程。安装目录：{entry.Game.InstallDir}"
                : $"请先启动游戏（进程名 {entry.Game.ProcessName}.exe），再回到「任意进程」页选择它。";
            Log($"已选择游戏「{entry.Game.Name}」，但未找到运行中的 {entry.Game.ProcessName}.exe。");
        }
    }

    private static int? FindRunningProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        try
        {
            var matches = Process.GetProcessesByName(processName);
            try
            {
                return matches.Length == 0 ? null : matches[0].Id;
            }
            finally
            {
                foreach (var m in matches) m.Dispose();
            }
        }
        catch { return null; }
    }

    // ========================================================================
    //  目标显示
    // ========================================================================

    private void SetTarget(int pid, string name, GameInfo? game)
    {
        _targetPid = pid;
        _targetName = name;
        TxtTarget.Text = $"{name}  (PID {pid})";

        if (game is not null)
        {
            TxtTargetGame.Text = $"游戏：{game.Name}（{game.PlatformName}）"
                               + (game.ConfigFiles.Count > 0 ? $"，已找到 {game.ConfigFiles.Count} 个配置文件" : "，未找到配置文件");
        }
        else
        {
            TxtTargetGame.Text = "未关联游戏库信息。仍可正常采样，报告里只有系统级建议，没有画质配置解读。";
        }

        Log($"目标已设为：{name} (PID {pid}){(game is null ? "" : $"（游戏：{game.Name}）")}");
    }

    // ========================================================================
    //  采样
    // ========================================================================

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_sampler is { IsRunning: true })
        {
            Log("采样已在进行中。");
            return;
        }
        if (_targetPid is null)
        {
            MessageBox.Show("请先在左侧选择一个进程或游戏。", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!double.TryParse(TxtInterval.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var interval)
            || interval < 0.2 || interval > 60)
        {
            MessageBox.Show("采样间隔请填 0.2 ~ 60 之间的秒数。", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double? duration = null;
        var durationText = TxtDuration.Text.Trim();
        if (durationText.Length > 0)
        {
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0)
            {
                MessageBox.Show("采集时长要么留空（手动停止），要么填一个大于 0 的秒数。", "wiisl",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            duration = d;
        }

        var enableFps = ChkFps.IsChecked == true;
        var options = new SamplingOptions
        {
            ProcessId = _targetPid.Value,
            ProcessName = _targetName,
            IntervalSeconds = interval,
            DurationSeconds = duration,
            StopWhenProcessExits = ChkStopOnExit.IsChecked == true,
            EnableFps = enableFps,
        };

        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = false;
        TxtSampleStatus.Text = "正在初始化采集器…";

        try
        {
            // 构造采样器会逐个 Probe（含一次 nvidia-smi，约 100ms），放后台避免界面卡顿
            var sampler = await Task.Run(() => new PerformanceSampler(enableFps));

            _sampler?.Dispose();
            _sampler = sampler;
            sampler.ProgressChanged += OnSamplerProgress;
            sampler.Completed += OnSamplerCompleted;

            ResetChart();
            foreach (var key in ReadoutKeys) SetReadout(key, "—");

            sampler.Start(options);
            if (sampler.CurrentSession is { } s) s.Game = _selectedGame;

            BtnStop.IsEnabled = true;
            BtnQuickAnalyze.IsEnabled = true;
            BarProgress.IsIndeterminate = duration is null;
            BarProgress.Value = 0;

            foreach (var st in sampler.CollectorStatuses.Where(x => !x.Available))
                Log($"[降级] {st.Name} 不可用：{st.Detail}");

            Log(duration is null
                ? $"开始采样：{_targetName} (PID {_targetPid})，间隔 {interval}s，手动停止。"
                : $"开始采样：{_targetName} (PID {_targetPid})，间隔 {interval}s，时长 {duration}s。");
        }
        catch (Exception ex)
        {
            BtnStart.IsEnabled = true;
            TxtSampleStatus.Text = "启动失败。";
            Log($"启动采样失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_sampler is not { IsRunning: true })
        {
            Log("当前没有在采样。");
            return;
        }
        _sampler.Stop();
        BtnStop.IsEnabled = false;
        TxtSampleStatus.Text = "正在停止…";
        Log("已请求停止采样。");
    }

    private async void BtnQuickAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_sampler is { IsRunning: true })
        {
            _sampler.Stop();
            TxtSampleStatus.Text = "正在停止并等待采集器收尾…";
            await _sampler.WaitForCompletionAsync();
        }
        GenerateReport();
    }

    private void OnSamplerProgress(object? sender, SampleProgressEventArgs e)
    {
        if (_closing) return;
        var sample = e.Sample;
        var index = e.SampleIndex;
        var elapsed = e.ElapsedSeconds;

        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;

            SetReadout("CPU 占用", Percent(sample.CpuTotalPercent));
            SetReadout("CPU 频率", Mhz(sample.CpuFrequencyMhz));
            SetReadout("CPU 温度", Temp(sample.CpuPackageTempC));
            SetReadout("CPU 功耗", Watts(sample.CpuPackagePowerW));

            SetReadout("GPU 占用", Percent(sample.GpuUtilPercent));
            SetReadout("GPU 频率", Mhz(sample.GpuClockMhz));
            SetReadout("GPU 温度", Temp(sample.GpuTempC));
            SetReadout("GPU 功耗", Watts(sample.GpuPowerW));

            SetReadout("进程内存", Mb(sample.ProcessWorkingSetMb));
            SetReadout("系统可用内存", Mb(sample.SystemAvailableMemoryMb));
            SetReadout("磁盘占用", Percent(sample.DiskPercent));
            SetReadout("页面文件", Percent(sample.PageFilePercent));

            var fpsPart = sample.Fps.HasValue ? $"，{sample.Fps.Value:F1} FPS" : "";
            TxtSampleStatus.Text = $"采样中… 样本 {index + 1}，已采 {elapsed:F1} 秒{fpsPart}";
            BarProgress.IsIndeterminate = true;

            AppendChartPoint(elapsed, sample.CpuTotalPercent, sample.GpuUtilPercent);
        });
    }

    private void OnSamplerCompleted(object? sender, SampleSession session)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;

            _session = session;
            if (sender is PerformanceSampler sp)
            {
                sp.ProgressChanged -= OnSamplerProgress;
                sp.Completed -= OnSamplerCompleted;
            }

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            BtnQuickAnalyze.IsEnabled = session.Samples.Count > 0;
            BtnGenerate.IsEnabled = session.Samples.Count > 0;
            BarProgress.IsIndeterminate = false;
            BarProgress.Value = 100;

            var actual = (session.EndedAt - session.StartedAt).TotalSeconds;
            TxtSampleStatus.Text = session.Samples.Count == 0
                ? $"采样结束：没有采到任何样本（停止原因：{StopReasonText(session.StopReason)}）。"
                : $"采样结束：{session.Samples.Count} 个样本，时长 {actual:F1} 秒，间隔 {session.IntervalSeconds:F1}s，"
                  + $"停止原因：{StopReasonText(session.StopReason)}"
                  + (session.FpsSampleCount > 0 ? $"，帧率样本 {session.FpsSampleCount} 个" : "，无帧率数据");

            Log($"采样结束：{session.Samples.Count} 个样本 / {actual:F1} 秒 / 停止原因 {session.StopReason}");
            foreach (var w in session.Warnings.Distinct().Take(15)) Log($"  [告警] {w}");

            if (session.Samples.Count == 0)
            {
                MessageBox.Show(
                    "这次采样一个样本都没采到。\n\n"
                    + "常见原因：目标进程在采样开始前就退出了，或采集器全部不可用。\n"
                    + "请点「重新检测环境」看采集能力探测结果。",
                    "wiisl", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (session.Samples.Count < 5)
            {
                Log("提示：样本少于 5 个，诊断结论的可信度很低（报告里也会这样标注）。");
            }
        });
    }

    private static string StopReasonText(string reason) => reason switch
    {
        "Manual" => "手动停止",
        "Duration" => "到达设定时长",
        "ProcessExited" => "目标进程已退出",
        "Error" => "异常终止",
        _ => reason,
    };

    // ========================================================================
    //  实时读数 / 曲线
    // ========================================================================

    private void BuildReadouts()
    {
        GridLive.Children.Clear();
        _readouts.Clear();

        foreach (var key in ReadoutKeys)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 2, 10, 6) };
            panel.Children.Add(new TextBlock
            {
                Text = key,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            });
            var value = new TextBlock
            {
                Text = "—",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            };
            panel.Children.Add(value);
            _readouts[key] = value;
            GridLive.Children.Add(panel);
        }
    }

    private void SetReadout(string key, string text)
    {
        if (_readouts.TryGetValue(key, out var tb)) tb.Text = text;
    }

    private void InitChart()
    {
        try
        {
            Plot.Plot.Title("采样时间序列");
            Plot.Plot.XLabel("时间（秒）");
            Plot.Plot.YLabel("占用率 %");
            Plot.Refresh();
        }
        catch (Exception ex)
        {
            Log($"曲线初始化失败（不影响采样与报告）：{ex.Message}");
        }
    }

    private void ResetChart()
    {
        _chartX.Clear();
        _chartCpu.Clear();
        _chartGpu.Clear();
        try
        {
            Plot.Plot.Clear();
            Plot.Refresh();
        }
        catch { /* 曲线不是关键路径 */ }
    }

    private void AppendChartPoint(double x, double? cpu, double? gpu)
    {
        _chartX.Add(x);
        _chartCpu.Add(cpu ?? double.NaN);
        _chartGpu.Add(gpu ?? double.NaN);

        // 1Hz 采样跑几小时也就几千点，但重画整条曲线是 O(n)，超过这个量级就只保留最近一段
        const int maxPoints = 3600;
        if (_chartX.Count > maxPoints)
        {
            var drop = _chartX.Count - maxPoints;
            _chartX.RemoveRange(0, drop);
            _chartCpu.RemoveRange(0, drop);
            _chartGpu.RemoveRange(0, drop);
        }

        try
        {
            Plot.Plot.Clear();
            Plot.Plot.Add.Scatter(_chartX.ToArray(), _chartCpu.ToArray());
            Plot.Plot.Add.Scatter(_chartX.ToArray(), _chartGpu.ToArray());
            Plot.Plot.Axes.AutoScale();
            Plot.Refresh();
        }
        catch { /* 曲线不是关键路径，画不出来也不能影响采样 */ }
    }

    // ========================================================================
    //  报告
    // ========================================================================

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.Samples.Count == 0)
        {
            MessageBox.Show("还没有可用的采样数据，请先采样。", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var formats = SelectedFormats();
        if (formats.Count == 0)
        {
            MessageBox.Show("请至少勾选一种输出格式。", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnGenerate.IsEnabled = false;
        TxtReportStatus.Text = "正在分析并生成报告…";

        var outputDir = TxtOutputDir.Text.Trim();
        var autoOpen = ChkAutoOpen.IsChecked == true;

        try
        {
            var session = _session;
            var game = _selectedGame ?? session.Game;

            var report = await Task.Run(() => ReportBuilder.Build(session, game));

            // 写文件放后台；注意 Log() 碰控件，只能在 UI 线程调，所以先收集消息再回 UI 线程输出。
            var messages = new List<string>();
            var written = await Task.Run(() => ReportBuilder.WriteAll(report, outputDir, formats, messages));

            foreach (var m in messages) Log(m);
            ShowFindings(report);

            if (written.Count == 0)
            {
                TxtReportStatus.Text = "报告生成失败，看底部日志。";
            }
            else
            {
                TxtReportStatus.Text = $"已生成 {written.Count} 个文件到 {outputDir}";
                Log("── 报告已生成 ──");
                foreach (var item in written) Log($"[{ReportBuilder.FormatName(item.Format)}] {item.Path}");

                if (autoOpen) OpenDirectory(outputDir);
            }
        }
        catch (Exception ex)
        {
            TxtReportStatus.Text = "分析过程出错。";
            Log($"生成报告失败：{ex.GetType().Name} {ex.Message}");
            MessageBox.Show($"生成报告失败：\n\n{ex.Message}", "wiisl",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnGenerate.IsEnabled = true;
        }
    }

    private List<ReportFormat> SelectedFormats()
    {
        var list = new List<ReportFormat>();
        if (ChkMd.IsChecked == true) list.Add(ReportFormat.Markdown);
        if (ChkPdf.IsChecked == true) list.Add(ReportFormat.Pdf);
        if (ChkCsv.IsChecked == true) list.Add(ReportFormat.Csv);
        if (ChkJson.IsChecked == true) list.Add(ReportFormat.Json);
        return list;
    }

    private void GenerateReport()
    {
        if (_session is null || _session.Samples.Count == 0)
        {
            TxtReportStatus.Text = "没有采到样本，无法生成报告。";
            Log("没有采到样本，已取消生成报告。");
            return;
        }
        BtnGenerate_Click(this, new RoutedEventArgs());
    }

    // ========================================================================
    //  结论展示
    // ========================================================================

    private sealed class FindingView
    {
        public Brush Background { get; init; } = Brushes.White;
        public Brush BorderBrush { get; init; } = Brushes.Gray;
        public Brush BadgeBackground { get; init; } = Brushes.Gray;
        public string SeverityText { get; init; } = "";
        public string Title { get; init; } = "";
        public string Evidence { get; init; } = "";
        public string Explanation { get; init; } = "";
        public string Recommendations { get; init; } = "";
        public string ExpectedGain { get; init; } = "";
    }

    private void ShowFindings(DiagnosisReport report)
    {
        var findings = report.Findings ?? new List<Finding>();
        var views = new List<FindingView>(findings.Count);

        foreach (var f in findings)
        {
            var (bg, border, badge, text) = SeverityStyle(f.Severity);
            views.Add(new FindingView
            {
                Background = bg,
                BorderBrush = border,
                BadgeBackground = badge,
                SeverityText = text,
                Title = f.Title,
                Evidence = f.Evidence.Count > 0 ? "实测数据：" + string.Join("；", f.Evidence) : "",
                Explanation = f.Explanation,
                Recommendations = f.Recommendations.Count > 0
                    ? "改进方法：\n" + string.Join("\n", f.Recommendations.Select((r, i) => $"  {i + 1}. {r}"))
                    : "",
                ExpectedGain = string.IsNullOrWhiteSpace(f.ExpectedGain) ? "" : $"预期收益：{f.ExpectedGain}",
            });
        }

        LstFindings.ItemsSource = views;

        var s = report.Summary;
        var counts = findings.GroupBy(f => f.Severity)
            .OrderByDescending(g => SeverityRank(g.Key))
            .Select(g => $"{SeverityText(g.Key)} {g.Count()}");
        var summary = $"{s.SampleCount} 个样本 / {s.DurationSeconds:F1} 秒　瓶颈判定：{s.BottleneckVerdict}　"
                    + $"结论分布：{string.Join("，", counts)}";
        if (report.Game is not null)
            summary = $"游戏：{report.Game.Name}（{report.Game.PlatformName}）　" + summary;
        if (s.FpsAvg is { } fpsAvg)
            summary += $"　平均帧率 {fpsAvg:F1} FPS";
        TxtFindingsSummary.Text = summary;
    }

    private static int SeverityRank(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => 5,
        FindingSeverity.Warning => 4,
        FindingSeverity.Minor => 3,
        FindingSeverity.Info => 2,
        FindingSeverity.Good => 1,
        _ => 0,
    };

    private static string SeverityText(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => "严重",
        FindingSeverity.Warning => "警告",
        FindingSeverity.Minor => "轻微",
        FindingSeverity.Info => "提示",
        FindingSeverity.Good => "良好",
        _ => "未知",
    };

    private static (Brush Background, Brush Border, Brush Badge, string Text) SeverityStyle(FindingSeverity s)
    {
        static Brush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        return s switch
        {
            FindingSeverity.Critical => (B(0xFE, 0xF2, 0xF2), B(0xFC, 0xA5, 0xA5), B(0xDC, 0x26, 0x26), "严重"),
            FindingSeverity.Warning => (B(0xFF, 0xFB, 0xEB), B(0xFC, 0xD3, 0x4D), B(0xD9, 0x77, 0x06), "警告"),
            FindingSeverity.Minor => (B(0xF8, 0xFA, 0xFC), B(0xCB, 0xD5, 0xE1), B(0x0E, 0xA5, 0xE9), "轻微"),
            FindingSeverity.Good => (B(0xEC, 0xFD, 0xF5), B(0x6E, 0xE7, 0xB7), B(0x05, 0x96, 0x69), "良好"),
            _ => (B(0xF1, 0xF5, 0xF9), B(0xCB, 0xD5, 0xE1), B(0x64, 0x74, 0x8B), "提示"),
        };
    }

    // ========================================================================
    //  输出目录
    // ========================================================================

    private static string DefaultOutputDirectory()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs)) docs = AppContext.BaseDirectory;
        return Path.Combine(docs, "游戏性能诊断报告");
    }

    private void BtnBrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择报告输出目录",
                InitialDirectory = Directory.Exists(TxtOutputDir.Text) ? TxtOutputDir.Text : DefaultOutputDirectory(),
            };
            if (dialog.ShowDialog(this) == true) TxtOutputDir.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            Log($"打开目录选择框失败：{ex.Message}");
        }
    }

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e) => OpenDirectory(TxtOutputDir.Text.Trim());

    private void OpenDirectory(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开目录失败：{ex.Message}");
        }
    }

    // ========================================================================
    //  格式化与日志
    // ========================================================================

    private static string Percent(double? v) => v.HasValue ? $"{v.Value:F1} %" : "—";
    private static string Mhz(double? v) => v.HasValue ? $"{v.Value:F0} MHz" : "—";
    private static string Temp(double? v) => v.HasValue ? $"{v.Value:F1} ℃" : "—";
    private static string Watts(double? v) => v.HasValue ? $"{v.Value:F1} W" : "—";
    private static string Mb(double? v) => v.HasValue ? $"{v.Value:F0} MB" : "—";

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }
        catch { /* 日志失败不能影响主流程 */ }
    }
}
