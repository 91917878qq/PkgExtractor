using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace PkgExtractor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowFatal(ex);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception ex)
    {
        try
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PkgExtractor-crash.log");
            System.IO.File.AppendAllText(log, $"[{DateTime.Now:O}] {ex}\r\n\r\n");
        }
        catch
        {
            // logging is best-effort
        }

        MessageBox.Show(
            ex.ToString(),
            "PKG Extractor — 未处理的异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
