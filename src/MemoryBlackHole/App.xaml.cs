using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MemoryBlackHole
{
    /// <summary>程序入口。</summary>
    public partial class App : Application
    {
        private static readonly string LogFile = Path.Combine(
            Path.GetTempPath(), "memoryblackhole.log");

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Log("=== MemoryBlackHole 启动 ===");

            // 全局异常捕获：任何未处理异常都【记录到日志文件 + 弹窗】，绝不用 Handled=true 吞掉。
            DispatcherUnhandledException += (_, args) =>
            {
                Log("DispatcherUnhandledException: " + args.Exception);
                MessageBox.Show("程序发生错误：\n" + args.Exception.ToString(),
                    "记忆黑洞", MessageBoxButton.OK, MessageBoxImage.Error);
                // 不设为 Handled，让程序继续（窗口若已创建则保留，避免静默消失）
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Log("AppDomain.UnhandledException: " + args.ExceptionObject);
                MessageBox.Show("程序发生严重错误：\n" + args.ExceptionObject,
                    "记忆黑洞", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            try
            {
                // 显式创建并显示主窗口（不再依赖 StartupUri，确保窗口一定创建）
                var win = new Views.MainWindow();
                win.Show();
                Log("MainWindow 已创建并显示");
            }
            catch (Exception ex)
            {
                Log("启动 MainWindow 失败: " + ex);
                MessageBox.Show("启动失败：\n" + ex + "\n\n详细日志：" + LogFile,
                    "记忆黑洞", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
