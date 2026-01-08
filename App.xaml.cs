using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FfmpegDrop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, ex) =>
        {
            try
            {
                File.AppendAllText("crash.log",
                    $"[DispatcherUnhandledException] {DateTime.Now}\n{ex.Exception}\n\n");
            }
            catch { }

            // prevent hard exit so you can see/log it
            ex.Handled = true;
            MessageBox.Show(ex.Exception.ToString(), "Unhandled UI Exception");
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            try
            {
                File.AppendAllText("crash.log",
                    $"[UnhandledException] {DateTime.Now}\n{ex.ExceptionObject}\n\n");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            try
            {
                File.AppendAllText("crash.log",
                    $"[UnobservedTaskException] {DateTime.Now}\n{ex.Exception}\n\n");
            }
            catch { }

            ex.SetObserved();
        };
    }
}

