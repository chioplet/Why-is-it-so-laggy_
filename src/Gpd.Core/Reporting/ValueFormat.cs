using System.Globalization;

namespace Gpd.Core.Reporting;

/// <summary>
/// 报告数字格式的唯一入口。全项目的 Markdown / CSV / PDF 输出都必须走这里，保证格式统一：
/// 占用与百分比 1 位小数、频率 1 位小数、温度 1 位小数、功耗 1 位小数、内存 0 位小数。
/// <para>
/// 【诚实原则】任何 <c>null</c> 都表示"本机采集不到"，一律显示为 <see cref="NoData"/>，
/// 绝不用 0 或任何默认值冒充实测数据。
/// </para>
/// </summary>
public static class ValueFormat
{
    /// <summary>采集不到数据时的统一文案（报告里到处都在用，不要改成空串）。</summary>
    public const string NoData = "本机采集不到";

    /// <summary>百分比 / 占用率：1 位小数，例如 <c>92.4%</c>。</summary>
    public static string Percent(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + "%"
            : NoData;

    /// <summary>频率：1 位小数，例如 <c>2450.0 MHz</c>。</summary>
    public static string Mhz(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + " MHz"
            : NoData;

    /// <summary>温度：1 位小数，例如 <c>78.5 ℃</c>。</summary>
    public static string Temp(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + " ℃"
            : NoData;

    /// <summary>功耗：1 位小数，例如 <c>63.2 W</c>。</summary>
    public static string Watt(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + " W"
            : NoData;

    /// <summary>内存：0 位小数，例如 <c>8192 MB</c>。</summary>
    public static string Memory(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F0", CultureInfo.InvariantCulture) + " MB"
            : NoData;

    /// <summary>帧率：1 位小数，例如 <c>58.3 FPS</c>。</summary>
    public static string Fps(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture) + " FPS"
            : NoData;

    /// <summary>帧生成时间：2 位小数，例如 <c>17.15 ms</c>。</summary>
    public static string Ms(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F2", CultureInfo.InvariantCulture) + " ms"
            : NoData;

    /// <summary>普通数字：1 位小数。</summary>
    public static string Number(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
            : NoData;

    /// <summary>普通数字：指定小数位（0-6 位）。占位符类的指标（采样间隔、队列长度）需要它。</summary>
    public static string Number(double? value, int digits) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F" + Math.Clamp(digits, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : NoData;

    /// <summary>普通数字：0 位小数（用于计数类指标）。</summary>
    public static string Number0(double? value) =>
        value.HasValue && IsUsable(value.Value)
            ? value.Value.ToString("F0", CultureInfo.InvariantCulture)
            : NoData;

    /// <summary>整数计数（本身一定存在，不是"采不到"）。</summary>
    public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>把毫秒时长格式化成人类可读文本。</summary>
    public static string Duration(double seconds)
    {
        if (!IsUsable(seconds) || seconds <= 0)
        {
            return "0 秒";
        }

        return seconds < 60
            ? seconds.ToString("F1", CultureInfo.InvariantCulture) + " 秒"
            : $"{Math.Floor(seconds / 60):F0} 分 {seconds % 60:F1} 秒";
    }

    private static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
