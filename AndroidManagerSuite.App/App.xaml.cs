using System.Configuration;
using System.Data;
using System.Windows;

namespace AndroidManagerSuite.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (s, ev) => 
        {
            MessageBox.Show(ev.Exception.ToString(), "Unhandled UI Exception");
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
        {
            MessageBox.Show(ev.ExceptionObject.ToString(), "Unhandled Domain Exception");
        };
        TaskScheduler.UnobservedTaskException += (s, ev) => 
        {
            MessageBox.Show(ev.Exception.ToString(), "Unhandled Task Exception");
            ev.SetObserved();
        };
    }
}

