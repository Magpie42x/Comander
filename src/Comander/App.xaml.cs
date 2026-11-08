using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Comander;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };
        base.OnStartup(e);
    }

    static void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show("Comander упал при запуске:\n\n" + e.Exception.Message +
                        "\n\nПодробности записаны в comander-crash.log рядом с exe.",
                        "Comander", MessageBoxButton.OK, MessageBoxImage.Error);
        // Handled не ставим — пусть завершится, журнал уже записан
    }

    static void Log(Exception? ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "comander-crash.log"),
                DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + "\n" + (ex?.ToString() ?? "unknown"));
        }
        catch { }
    }
}
