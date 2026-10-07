using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gpd.Core.Reporting;

/// <summary>
/// JSON 报告：完整的机器可读导出，包含 meta / machine / game / summary / findings / graphics / cores / samples。
/// 中文不转义（<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>），2 空格缩进，便于人工查看与脚本解析。
/// 字段值为 null 表示本机采集不到该项，不是 0。
/// </summary>
public sealed class JsonReportWriter : IReportWriter
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>序列化用的选项（自测里做往返校验时也用同一份）。</summary>
    public static JsonSerializerOptions SerializerOptions => Options;

    public string Write(DiagnosisReport report, string outputDirectory, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);

        var directory = ReportFileHelper.EnsureDirectory(outputDirectory);
        var path = Path.Combine(directory, ReportFileHelper.BuildFileName(report, "_report", "json"));
        File.WriteAllText(path, Build(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(path);
    }

    /// <summary>生成 JSON 文本。</summary>
    public static string Build(DiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var session = report.Session;
        var machine = session?.Machine;

        // 显式声明成 object? 才能对 "null : 匿名类型" 使用目标类型条件表达式
        object? machineDto = machine is null ? null : new
        {
            osName = machine.OsName,
            osVersion = machine.OsVersion,
            osBuild = machine.OsBuild,
            cpuName = machine.CpuName,
            cpuPhysicalCores = machine.CpuPhysicalCores,
            cpuLogicalProcessors = machine.CpuLogicalProcessors,
            cpuNominalMhz = machine.CpuNominalMhz,
            totalMemoryMb = machine.TotalMemoryMb,
            isElevated = machine.IsElevated,
            powerPlanName = machine.PowerPlanName,
            gameModeEnabled = machine.GameModeEnabled,
            hardwareGpuSchedulingEnabled = machine.HardwareGpuSchedulingEnabled,
            nvidiaDriverVersion = machine.NvidiaDriverVersion,
            gpus = machine.Gpus?.Select(g => new
            {
                name = g.Name,
                vramMb = g.VramMb,
                driverVersion = g.DriverVersion,
                isVirtualDisplayAdapter = g.IsVirtualDisplayAdapter,
            }).ToArray(),
            capabilities = machine.Capabilities?.Select(c => new
            {
                name = c.Name,
                available = c.Available,
                detail = c.Detail,
            }).ToArray(),
        };

        var game = session?.Game;
        object? gameDto = game is null ? null : new
        {
            name = game.Name,
            platform = game.PlatformName,
            appId = game.AppId,
            installDir = game.InstallDir,
            executablePath = game.ExecutablePath,
            discoverySource = game.DiscoverySource,
            lastPlayed = game.LastPlayed,
            playTimeHours = game.PlayTime?.TotalHours,
            configFiles = game.ConfigFiles?.Select(c => new
            {
                path = c.Path,
                format = c.Format,
                purpose = c.Purpose,
                sizeBytes = c.SizeBytes,
                lastWriteTime = c.LastWriteTime,
                entryCount = c.Entries?.Count ?? 0,
                error = c.Error,
            }).ToArray(),
        };

        var payload = new
        {
            meta = new
            {
                toolVersion = report.ToolVersion,
                generatedAt = report.GeneratedAt == default ? (DateTimeOffset?)null : report.GeneratedAt,
                processName = session?.ProcessName,
                processId = session?.ProcessId,
                processPath = session?.ProcessPath,
                durationSeconds = report.Summary?.DurationSeconds,
                sampleCount = session?.Samples?.Count ?? 0,
                fpsSampleCount = session?.FpsSampleCount ?? 0,
                intervalSeconds = session?.IntervalSeconds,
                startedAt = session?.StartedAt == default ? (DateTimeOffset?)null : session?.StartedAt,
                endedAt = session?.EndedAt,
                stopReason = session?.StopReason,
                bottleneckVerdict = report.Summary?.BottleneckVerdict,
                findingCount = report.Findings?.Count ?? 0,
                findingCounts = CountBySeverity(report.Findings),
                selfTest = IsSelfTest(report),
            },
            machine = machineDto,
            game = gameDto,
            summary = report.Summary,
            findings = report.Findings?.Select(f => new
            {
                severity = f.Severity.ToString(),
                severityZh = SeverityZh(f.Severity),
                category = f.Category,
                title = f.Title,
                evidence = f.Evidence,
                explanation = f.Explanation,
                recommendations = f.Recommendations,
                expectedGain = f.ExpectedGain,
                configEvidence = f.ConfigEvidence,
            }).ToArray(),
            graphics = report.GraphicsInsights?.Select(g => new
            {
                settingName = g.SettingName,
                sourceKey = g.SourceKey,
                rawValue = g.RawValue,
                normalizedValue = g.NormalizedValue,
                performanceImpact = g.PerformanceImpact,
                comment = g.Comment,
            }).ToArray(),
            cores = report.Cores?.Select(c => new
            {
                index = c.Index,
                label = c.Label,
                usagePercent = c.UsagePercent,
                tempC = c.TempC,
            }).ToArray(),
            warnings = session?.Warnings,
            samples = session?.Samples?.Select(s => new
            {
                elapsedSeconds = s.ElapsedSeconds,
                timestamp = s.Timestamp == default ? (DateTimeOffset?)null : s.Timestamp,
                cpuTotalPercent = s.CpuTotalPercent,
                cpuFrequencyMhz = s.CpuFrequencyMhz,
                cpuPerformancePercent = s.CpuPerformancePercent,
                cpuUtilityPercent = s.CpuUtilityPercent,
                cpuPackageTempC = s.CpuPackageTempC,
                cpuPackagePowerW = s.CpuPackagePowerW,
                cpuLogicalCount = s.CpuLogicalCount,
                cpuPerCorePercent = s.CpuPerCorePercent,
                cpuCoreTempC = s.CpuCoreTempC,
                gpuName = s.GpuName,
                gpuUtilPercent = s.GpuUtilPercent,
                gpuClockMhz = s.GpuClockMhz,
                gpuMemClockMhz = s.GpuMemClockMhz,
                gpuTempC = s.GpuTempC,
                gpuVoltageMv = s.GpuVoltageMv,
                gpuPowerW = s.GpuPowerW,
                gpuPowerLimitW = s.GpuPowerLimitW,
                gpuFanPercent = s.GpuFanPercent,
                gpuProcessDedicatedMemoryMb = s.GpuProcessDedicatedMemoryMb,
                gpuProcessSharedMemoryMb = s.GpuProcessSharedMemoryMb,
                gpuTotalDedicatedMemoryMb = s.GpuTotalDedicatedMemoryMb,
                gpuTotalDedicatedMemoryLimitMb = s.GpuTotalDedicatedMemoryLimitMb,
                processWorkingSetMb = s.ProcessWorkingSetMb,
                processPrivateMemoryMb = s.ProcessPrivateMemoryMb,
                processCpuPercent = s.ProcessCpuPercent,
                systemAvailableMemoryMb = s.SystemAvailableMemoryMb,
                systemCommittedPercent = s.SystemCommittedPercent,
                pageFilePercent = s.PageFilePercent,
                diskPercent = s.DiskPercent,
                diskQueueLength = s.DiskQueueLength,
                fpsSource = s.FpsSource,
                fps = s.Fps,
                fpsAverage = s.FpsAverage,
                frameTimeMs = s.FrameTimeMs,
                fps1PercentLow = s.Fps1PercentLow,
                fps01PercentLow = s.Fps01PercentLow,
                notes = s.Notes,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>本报告是否来自合成自测数据（自测会把这句话写进采样备注）。</summary>
    private static bool IsSelfTest(DiagnosisReport report)
    {
        var samples = report.Session?.Samples;
        if (samples is null)
        {
            return false;
        }

        foreach (var sample in samples)
        {
            if (sample.Notes is null)
            {
                continue;
            }

            foreach (var note in sample.Notes)
            {
                if (note.Contains("self-test", StringComparison.OrdinalIgnoreCase) ||
                    note.Contains("自测", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, int> CountBySeverity(List<Finding>? findings)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Critical"] = 0,
            ["Warning"] = 0,
            ["Minor"] = 0,
            ["Info"] = 0,
            ["Good"] = 0,
        };

        if (findings is null)
        {
            return result;
        }

        foreach (var finding in findings)
        {
            var key = finding.Severity.ToString();
            result[key] = result.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return result;
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
}
