using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using FanPilot.Ipc;
using FanPilot.Settings;
using FanPilot.Startup;
using FanPilot.UI;

namespace FanPilot;

/// <summary>The window and tray icon. Runs as the user; the fans are driven by the FanPilot service.</summary>
public partial class App : Application
{
    readonly LaunchOptions _options;
    readonly SingleInstance _instance;

    readonly AppSettings _settings;
    ServiceClient _client = null!;
    MainWindow _window = null!;
    TrayIcon _tray = null!;
    bool _exiting;

    public App(LaunchOptions options, AppSettings settings, SingleInstance instance)
    {
        _options = options;
        _settings = settings;
        _instance = instance;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;

        _client = new ServiceClient();

        var vm = new MainViewModel(_settings, _client);
        _client.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => vm.OnConnectionChanged(connected));
        _client.MessageReceived += message => Dispatcher.BeginInvoke(() => vm.OnMessage(message));

        _window = new MainWindow(vm);
        _window.Closing += OnWindowClosing;
        ThemeService.Apply(_settings.Theme, _window);
        vm.ThemeChanged += theme => ThemeService.Apply(theme, _window);

        _tray = new TrayIcon(vm, ShowWindow, ExitApp);
        _client.Start();

        var connectGrace = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        connectGrace.Tick += (_, _) =>
        {
            connectGrace.Stop();
            vm.MarkConnectTimeout();
        };
        connectGrace.Start();

        SessionEnding += (_, _) => ExitApp();
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowWindow));

        if (!_options.Tray) ShowWindow();
    }

    void ShowWindow()
    {
        if (_exiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // Activate alone doesn't always win focus when another app is in front.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        if (_settings.CloseToTray) _window.Hide();
        else ExitApp();
    }

    /// <summary>Closes the window and tray icon. The service keeps the fans in their current mode.</summary>
    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _client.Dispose();
        _tray.Dispose();
        _instance.Dispose();
        _window.Close();
        Shutdown();
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"UI exception: {e.Exception}");
        e.Handled = true;
    }
}
