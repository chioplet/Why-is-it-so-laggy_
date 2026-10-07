using System.Diagnostics;
using System.Text;

namespace Gpd.Core.Collect;

/// <summary>
/// 采集模块共用的小工具。
/// 全部是"只读"操作：只查询状态、只读取数据，绝不修改系统或游戏文件。
/// </summary>
public static class CollectUtil
{
    /// <summary>
    /// 【必须最先执行】注册 CodePages 编码提供程序。
    /// .NET (Core) 默认只带 ASCII/UTF-8/UTF-16/UTF-32/Latin1，**不含 GBK(936)**；
    /// 不注册的话 <c>Encoding.GetEncoding(936)</c> 会抛 ArgumentException，
    /// 中文 Windows 上所有控制台程序的输出解码都会退回 UTF-8 → 乱码
    /// （实测 powercfg 的「卓越性能」显示成「׿Խ����」）。
    /// </summary>
    static CollectUtil()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 已注册或平台不支持；OemEncoding 里还有兜底 */ }
    }

    /// <summary>本进程是否以管理员身份运行。管理员才能启动 PresentMon 的 ETW 内核会话。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在常见位置查找可执行文件。返回 null 表示没找到。
    /// </summary>
    public static string? FindExecutable(string fileName, params string?[] extraDirectories)
    {
        var candidates = new List<string>();

        foreach (var dir in extraDirectories)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                candidates.Add(Path.Combine(dir!, fileName));
        }

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system32))
            candidates.Add(Path.Combine(system32, fileName));

        // NVIDIA 驱动安装位置（nvidia-smi 通常在这里）
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", fileName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "NVIDIA Corporation", "NVSMI", fileName));

        // 驱动直接放在 System32 之外的情况：扫一遍 PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), fileName)); } catch { /* 非法 PATH 项，忽略 */ }
        }

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { /* 忽略 */ }
        }
        return null;
    }

    /// <summary>
    /// 控制台程序（powercfg、ipconfig 这类）在中文 Windows 上输出的是 OEM 代码页（936=GBK），
    /// 不是 UTF-8。按 UTF-8 解码会得到乱码（实测 powercfg 的电源计划名变成了一串方块）。
    /// 这里从注册表读 OEMCP，读不到时退到 ANSI 代码页，再退 UTF-8。
    /// </summary>
    public static Encoding OemEncoding
    {
        get
        {
            if (_oemEncoding is not null) return _oemEncoding;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Nls\CodePage");
                if (key?.GetValue("OEMCP") is string cp && int.TryParse(cp, out var codePage) && codePage > 0)
                {
                    _oemEncoding = Encoding.GetEncoding(codePage);
                    return _oemEncoding;
                }
            }
            catch { /* 走下面的兜底 */ }

            // 兜底 1：中文系统几乎都是 936
            try { _oemEncoding = Encoding.GetEncoding(936); return _oemEncoding; }
            catch { /* 走下面的兜底 */ }

            // 兜底 2：当前区域设置的 ANSI 代码页
            try
            {
                var ansi = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (ansi > 0) { _oemEncoding = Encoding.GetEncoding(ansi); return _oemEncoding; }
            }
            catch { /* 走下面的兜底 */ }

            // 最后的兜底：UTF-8。此时中文可能乱码，OemDecodingAvailable 会报 false 供报告说明。
            _oemEncoding = Encoding.UTF8;
            return _oemEncoding;
        }
    }
    private static Encoding? _oemEncoding;

    /// <summary>
    /// OEM 代码页是否真的拿到了（而不是退化成 UTF-8）。
    /// 为 false 时报告里要提示"外部命令输出可能乱码"，而不是把乱码当正常数据展示。
    /// </summary>
    public static bool OemDecodingAvailable
    {
        get
        {
            var e = OemEncoding;
            return e.CodePage != Encoding.UTF8.CodePage;
        }
    }

    /// <summary>执行一个外部命令并抓取标准输出，按控制台 OEM 代码页解码。带超时保护，永不抛异常。</summary>
    public static string RunProcess(string exePath, string arguments, int timeoutMs, out string error)
        => RunProcess(exePath, arguments, timeoutMs, OemEncoding, out error);

    /// <summary>执行一个外部命令，按 UTF-8 解码输出（nvidia-smi 的输出是 UTF-8）。</summary>
    public static string RunProcessUtf8(string exePath, string arguments, int timeoutMs, out string error)
        => RunProcess(exePath, arguments, timeoutMs, Encoding.UTF8, out error);

    /// <summary>执行一个外部命令并抓取标准输出。带超时保护，永不抛异常。</summary>
    public static string RunProcess(string exePath, string arguments, int timeoutMs, Encoding encoding, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };
            using var p = Process.Start(psi);
            if (p is null) { error = "进程启动失败"; return ""; }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                error = $"命令超时（{timeoutMs}ms）：{Path.GetFileName(exePath)} {arguments}";
                return stdout;
            }
            if (p.ExitCode != 0)
                error = $"退出码 {p.ExitCode}：{stderr.Trim()}";
            return stdout;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return "";
        }
    }

    /// <summary>把可能为 NaN / Infinity 的值转成 null，避免污染统计与报告。</summary>
    public static double? Sanitize(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    /// <summary>压缩空白，便于把外部命令输出写进报告。</summary>
    public static string Collapse(string s)
        => string.Join(' ', s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
}
