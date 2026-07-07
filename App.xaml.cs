using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BODA.CMS
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 모니터링 앱은 UI 한 곳의 예외로 통째로 죽으면 안 된다 — 로그 남기고 계속 실행.
            // (드라이버 스레드 예외는 각 드라이버가 삼키고 Notification으로 보고하는 구조.)
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "BODA.CMS.crash.log");
            try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n"); }
            catch { /* 로그 실패는 무시 */ }

            MessageBox.Show(
                $"처리되지 않은 오류가 발생했습니다. 앱은 계속 실행됩니다.\n\n{e.Exception.Message}\n\n로그: {logPath}",
                "BODA.CMS 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
