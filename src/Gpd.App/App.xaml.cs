using System.Windows;
using System.Windows.Threading;

namespace Gpd.App;

/// <summary>
/// 应用入口。除了启动主窗口，主要职责是兜住未处理异常——
/// 这是一个需要管理员权限、会读性能计数器/调 nvidia-smi 的工具，
/// 任何一处外部依赖出问题都不应该让用户看到一个"程序已停止工作"。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 无界面模式：采集 → 分析 → 出报告，跑完就退出。给脚本/自动化验证用。
        if (e.Args.Any(a => string.Equals(a, "--cli", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = 1;
            try
            {
                exitCode = CliRunner.Run(e.Args);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CLI 运行失败：{ex}");
                Console.Out.Flush();
            }
            Shutdown(exitCode);
            return;
        }

        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowError("界面线程异常", e.Exception);
        e.Handled = true;   // 尽量让程序活下去，用户还能导出已采集的数据
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) ShowError("后台线程异常", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ShowError("后台任务异常", e.Exception);
        e.SetObserved();
    }

    private static void ShowError(string title, Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"{title}：\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "wiisl",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 连弹窗都失败就只能放弃提示了
        }
    }
}
