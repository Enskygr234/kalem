using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Kalem;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\Kalem.TekKopya", out bool created);
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(@"Local\Kalem.Goster").Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Kalem.Goster");
        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };

        var w = new MainWindow();
        MainWindow = w;
        w.Init(startHidden: e.Args.Contains("--hidden"));
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => w.OpenFromTray()), null, -1, false);
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "hata.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
