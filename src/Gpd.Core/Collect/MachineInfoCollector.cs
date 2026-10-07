using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace Gpd.Core.Collect;

/// <summary>
/// 静态机器信息采集：CPU 型号/核数、内存总量、显卡列表、系统版本、电源计划、游戏模式等。
/// 全部只读查询。
/// </summary>
public static class MachineInfoCollector
{
    public static MachineInfo Collect(List<CapabilityProbe> capabilities)
    {
        var info = new MachineInfo { Capabilities = capabilities };

        // ---- 操作系统 ----
        try
        {
            info.OsName = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
            using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (cv is not null)
            {
                var product = cv.GetValue("ProductName") as string;
                var display = cv.GetValue("DisplayVersion") as string;   // 例如 24H2
                var build = cv.GetValue("CurrentBuildNumber") as string;
                var ubr = cv.GetValue("UBR");
                if (!string.IsNullOrWhiteSpace(product)) info.OsName = product!;
                info.OsVersion = string.IsNullOrWhiteSpace(display) ? "" : display!;
                info.OsBuild = string.IsNullOrWhiteSpace(build) ? "" : (ubr is null ? build! : $"{build}.{ubr}");
            }
        }
        catch { /* 忽略 */ }

        // ---- CPU ----
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                info.CpuName = (o["Name"] as string)?.Trim() ?? "";
                info.CpuPhysicalCores = Convert.ToInt32(o["NumberOfCores"] ?? 0);
                info.CpuLogicalProcessors = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                info.CpuNominalMhz = Convert.ToDouble(o["MaxClockSpeed"] ?? 0);
                break;   // 多路 CPU 只取第一颗（消费级机器都是单颗）
            }
        }
        catch { /* 忽略 */ }

        // ---- 内存 ----
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                info.TotalMemoryMb = Math.Round(Convert.ToDouble(o["TotalPhysicalMemory"] ?? 0) / 1024.0 / 1024.0, 0);
                break;
            }
        }
        catch { /* 忽略 */ }

        // ---- 显卡 ----
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                var name = (o["Name"] as string)?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                double? vram = null;
                try
                {
                    var raw = Convert.ToDouble(o["AdapterRAM"] ?? 0);
                    if (raw > 0) vram = Math.Round(raw / 1024.0 / 1024.0, 0);
                }
                catch { /* AdapterRAM 在 4GB 以上会溢出成负数，忽略 */ }

                info.Gpus.Add(new GpuInfo
                {
                    Name = name,
                    VramMb = vram,
                    DriverVersion = (o["DriverVersion"] as string)?.Trim() ?? "",
                    IsVirtualDisplayAdapter = IsVirtualAdapter(name),
                });
            }
        }
        catch { /* 忽略 */ }

        // nvidia-smi 的显存与驱动版本更可靠，用它覆盖 NVIDIA 卡的记录。
        // 【实测教训】本机 nvidia-smi 617.14 **不支持 --format=xml**（退出码 2，stdout 只有
        // "Format modifier is not recognized."），所以这里必须和 GpuCollector 一样走 CSV 通路；
        // 否则 WMI 会把 8GB 显存溢出报成 4095MB、驱动版本留空。
        try
        {
            var smi = CollectUtil.FindExecutable("nvidia-smi.exe");
            if (smi is not null)
            {
                var csv = CollectUtil.RunProcessUtf8(smi,
                    "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits", 5000, out _);
                var gpus = GpuCollector.ParseGpus(csv, new[] { "name", "driver_version", "memory.total" }, out _);
                if (gpus.Count > 0)
                {
                    info.Gpus.RemoveAll(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
                    foreach (var g in gpus)
                    {
                        g.IsVirtualDisplayAdapter = false;
                        info.Gpus.Insert(0, g);
                    }
                    info.NvidiaDriverVersion = gpus[0].DriverVersion;
                }
            }
        }
        catch { /* 忽略 */ }

        // ---- 电源计划 ----
        try
        {
            var outText = CollectUtil.RunProcess("powercfg.exe", "/getactivescheme", 5000, out _);
            // 形如：电源方案 GUID: 381b4222-...  (平衡)
            var m = System.Text.RegularExpressions.Regex.Match(outText, @"\(([^)]+)\)\s*$");
            info.PowerPlanName = m.Success ? m.Groups[1].Value.Trim() : CollectUtil.Collapse(outText);
        }
        catch { /* 忽略 */ }

        // ---- 游戏模式 / 硬件加速 GPU 计划 ----
        try
        {
            using var gb = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\GameBar");
            if (gb?.GetValue("AllowAutoGameMode") is int am) info.GameModeEnabled = am != 0;
        }
        catch { /* 忽略 */ }

        try
        {
            using var gd = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (gd?.GetValue("HwSchMode") is int hw) info.HardwareGpuSchedulingEnabled = hw == 2;
        }
        catch { /* 忽略 */ }

        info.IsElevated = CollectUtil.IsElevated();
        return info;
    }

    /// <summary>判断是否是虚拟显示器适配器（本机装了 GameViewer Virtual Display Adapter）。</summary>
    public static bool IsVirtualAdapter(string name)
    {
        string[] hints = { "GameViewer", "Virtual Display", "Idd", "Parsec", "Spacedesk", "Mirror", "Basic Display" };
        return hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
