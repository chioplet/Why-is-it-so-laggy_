using System.IO;
using System.Text;

namespace Gpd.Core.Reporting;

/// <summary>
/// 报告文件命名与目录准备。文件名统一规则：<c>诊断报告_&lt;目标进程名&gt;_&lt;yyyyMMdd_HHmmss&gt;&lt;后缀&gt;.&lt;扩展名&gt;</c>。
/// 时间戳取自 <see cref="DiagnosisReport.GeneratedAt"/>，保证同一次分析写出的多个格式文件名一致。
/// </summary>
public static class ReportFileHelper
{
    /// <summary>确保输出目录存在，返回绝对路径。目录为空时退回到临时目录下的 GpdReports。</summary>
    public static string EnsureDirectory(string? outputDirectory)
    {
        var target = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Path.GetTempPath(), "GpdReports")
            : outputDirectory!;

        var full = Path.GetFullPath(target);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>把进程名里不能做文件名的字符替换掉。</summary>
    public static string SanitizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "未知进程";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(processName.Length);
        foreach (var ch in processName)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "未知进程" : result;
    }

    /// <summary>拼出报告文件名（不含目录）。</summary>
    public static string BuildFileName(DiagnosisReport report, string suffix, string extension)
    {
        ArgumentNullException.ThrowIfNull(report);

        var process = SanitizeProcessName(report.Session.ProcessName);
        var stamp = report.GeneratedAt == default ? DateTimeOffset.Now : report.GeneratedAt;
        return $"诊断报告_{process}_{stamp:yyyyMMdd_HHmmss}{suffix}.{extension}";
    }

    /// <summary>列出输出目录里同一次分析产生的同名报告文件（用于"附：原始数据文件清单"）。</summary>
    public static List<string> ListCompanionFiles(string directory, DiagnosisReport report)
    {
        var prefix = $"诊断报告_{SanitizeProcessName(report.Session.ProcessName)}_";
        try
        {
            return Directory.EnumerateFiles(directory, prefix + "*")
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
