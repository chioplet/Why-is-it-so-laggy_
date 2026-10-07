namespace Gpd.Core.Reporting;

/// <summary>
/// 按格式取报告写入器。PDF 需要 QuestPDF（只被 Gpd.App 引用），所以 <see cref="ReportFormat.Pdf"/>
/// 在这里返回 <c>null</c>：宿主应该改用 <c>Gpd.App.Reporting.PdfReportWriter</c>。
/// </summary>
public static class ReportWriterFactory
{
    /// <summary>PDF 不可用时给用户看的说明。</summary>
    public const string PdfRequiresAppHostHint =
        "PDF 报告由桌面程序（Gpd.App）里的 QuestPDF 写入器生成；命令行核心库不含 PDF 依赖。";

    /// <summary>Gpd.Core 自身能直接写出的格式。</summary>
    public static IReadOnlyList<ReportFormat> SupportedFormats { get; } =
        [ReportFormat.Markdown, ReportFormat.Csv, ReportFormat.Json];

    /// <summary>取写入器；不支持的格式返回 null（不抛异常，方便 UI 直接判断）。</summary>
    public static IReportWriter? Create(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => new MarkdownReportWriter(),
        ReportFormat.Csv => new CsvReportWriter(),
        ReportFormat.Json => new JsonReportWriter(),
        _ => null,
    };

    /// <summary>该格式是否由 Gpd.Core 直接支持。</summary>
    public static bool IsSupported(ReportFormat format) => format is ReportFormat.Markdown or ReportFormat.Csv or ReportFormat.Json;

    /// <summary>
    /// 一次写出全部受支持的格式，返回 格式 → 绝对路径。PDF 会被跳过（见 <see cref="PdfRequiresAppHostHint"/>）。
    /// </summary>
    public static Dictionary<ReportFormat, string> WriteAllSupported(DiagnosisReport report, string? outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = new Dictionary<ReportFormat, string>();
        foreach (var format in SupportedFormats)
        {
            var writer = Create(format);
            if (writer is null)
            {
                continue;
            }

            result[format] = writer.Write(report, outputDirectory ?? string.Empty, format);
        }

        return result;
    }
}
