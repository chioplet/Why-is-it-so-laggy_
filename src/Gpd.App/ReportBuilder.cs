using System.IO;
using System.Reflection;
using Gpd.App.Reporting;
using Gpd.Core;
using Gpd.Core.Analysis;
using Gpd.Core.Games;
using Gpd.Core.Reporting;

namespace Gpd.App;

/// <summary>
/// 从采样结果到「报告对象 + 落盘文件」的完整流水线。
/// 界面（MainWindow）与无界面模式（CliRunner）共用这一份，避免两条路径算出的结论不一致。
/// </summary>
public static class ReportBuilder
{
    /// <summary>
    /// 组装诊断报告。顺序不能换：<see cref="DiagnosisEngine.Analyze"/> 依赖 Summary 与 GraphicsInsights 已经填好。
    /// </summary>
    public static DiagnosisReport Build(SampleSession session, GameInfo? game)
    {
        ArgumentNullException.ThrowIfNull(session);

        var report = new DiagnosisReport
        {
            Session = session,
            Game = game,
            GeneratedAt = DateTimeOffset.Now,
            ToolVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
        };
        session.Game = game;

        report.Summary = SummaryCalculator.Calculate(session);
        report.GraphicsInsights = game is null
            ? new List<GraphicsSettingInsight>()
            : GraphicsSettingsAnalyzer.Analyze(game);
        report.Cores = BuildCoreSnapshots(session);
        report.Findings = DiagnosisEngine.Analyze(report);
        return report;
    }

    /// <summary>
    /// 逐核快照。报告里的列是「平均占用 / 最高温度」，所以这里就按平均值与最大值算，
    /// 不要塞最后一秒的瞬时值——那和列名对不上。
    /// </summary>
    public static List<CpuCoreSnapshot> BuildCoreSnapshots(SampleSession session)
    {
        var perCore = session.Samples
            .Where(s => s.CpuPerCorePercent is { Count: > 0 })
            .Select(s => s.CpuPerCorePercent!)
            .ToList();
        if (perCore.Count == 0) return new List<CpuCoreSnapshot>();

        var count = perCore.Max(c => c.Count);
        var result = new List<CpuCoreSnapshot>(count);

        for (var i = 0; i < count; i++)
        {
            var values = perCore.Where(c => i < c.Count).Select(c => c[i]).ToList();
            double? avg = values.Count > 0 ? Math.Round(values.Average(), 1) : null;

            double? maxTemp = null;
            foreach (var s in session.Samples)
            {
                if (s.CpuCoreTempC is { } map && map.TryGetValue(i, out var t))
                    maxTemp = maxTemp is null ? t : Math.Max(maxTemp.Value, t);
            }

            result.Add(new CpuCoreSnapshot
            {
                Index = i,
                Label = $"逻辑处理器 {i}",
                UsagePercent = avg,
                TempC = maxTemp,
            });
        }

        return result;
    }

    /// <summary>
    /// 按勾选的格式写报告。单个格式失败不影响其它格式，失败原因写进 <paramref name="messages"/>。
    /// </summary>
    /// <param name="failedFormats">
    /// 返回真正失败的格式。调用方据此决定退出码——请求了 pdf 却只写出 md 时，不该静默返回成功。
    /// </param>
    /// <returns>成功写出的 格式 → 路径。</returns>
    public static List<(ReportFormat Format, string Path)> WriteAll(
        DiagnosisReport report,
        string outputDirectory,
        IEnumerable<ReportFormat> formats,
        List<string>? messages = null,
        List<ReportFormat>? failedFormats = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(formats);

        var written = new List<(ReportFormat Format, string Path)>();

        foreach (var format in formats)
        {
            // 先算出这次要写的文件名，失败时才知道该清理哪个残留。
            // 写出器内部会自己决定扩展名，这里按同样的规则推一遍（时间戳到秒，正常不会撞车）。
            string[] before = [];
            try
            {
                before = Directory.Exists(outputDirectory)
                    ? Directory.GetFiles(outputDirectory)
                    : [];
            }
            catch
            {
                // 列目录失败不影响主流程，只是失去清理能力。
            }

            try
            {
                // ReportWriterFactory 不含 Pdf（QuestPDF 只被 Gpd.App 引用），所以 Pdf 自己 new
                var writer = format == ReportFormat.Pdf
                    ? new PdfReportWriter()
                    : ReportWriterFactory.Create(format);

                if (writer is null)
                {
                    messages?.Add($"[跳过] {FormatName(format)}：没有可用的写入器。");
                    failedFormats?.Add(format);
                    continue;
                }

                written.Add((format, writer.Write(report, outputDirectory, format)));
            }
            catch (Exception ex)
            {
                messages?.Add($"[失败] {FormatName(format)} 生成失败：{ex.GetType().Name} {ex.Message}");
                failedFormats?.Add(format);
                CleanupEmptyArtifacts(outputDirectory, before, messages);
            }
        }

        return written;
    }

    /// <summary>
    /// 删掉本次新产生、但大小为 0 的残留文件。
    /// QuestPDF 会先把输出文件建出来再抛异常（例如缺字体资源时），留下一个 0 字节的 .pdf——
    /// 比没有文件更糟：用户会以为报告生成成功了，双击却打不开。
    /// 只删"本次新增 + 0 字节"的文件，不动调用前就存在的任何东西。
    /// </summary>
    private static void CleanupEmptyArtifacts(string directory, string[] before, List<string>? messages)
    {
        if (!Directory.Exists(directory)) return;

        try
        {
            foreach (var path in Directory.GetFiles(directory))
            {
                if (before.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                if (new FileInfo(path).Length != 0) continue;

                File.Delete(path);
                messages?.Add($"[清理] 已删除生成失败留下的空文件：{Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            messages?.Add($"[清理] 空文件清理失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>解析 "md,pdf,csv,json" 这类格式列表。空列表表示全部支持。</summary>
    public static List<ReportFormat> ParseFormats(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return new List<ReportFormat> { ReportFormat.Markdown, ReportFormat.Pdf, ReportFormat.Csv, ReportFormat.Json };

        var result = new List<ReportFormat>();
        foreach (var raw in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            var format = token switch
            {
                "md" or "markdown" => ReportFormat.Markdown,
                "pdf" => ReportFormat.Pdf,
                "csv" => ReportFormat.Csv,
                "json" => ReportFormat.Json,
                _ => (ReportFormat?)null,
            };
            if (format is { } f && !result.Contains(f)) result.Add(f);
        }

        return result.Count > 0
            ? result
            : new List<ReportFormat> { ReportFormat.Markdown, ReportFormat.Pdf, ReportFormat.Csv, ReportFormat.Json };
    }

    public static string FormatName(ReportFormat f) => f switch
    {
        ReportFormat.Markdown => "Markdown",
        ReportFormat.Pdf => "PDF",
        ReportFormat.Csv => "CSV",
        ReportFormat.Json => "JSON",
        _ => f.ToString(),
    };
}
