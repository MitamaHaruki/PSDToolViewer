using System.Windows;
using System.Windows.Threading;
using PSDToolViewer.Services;
using PSDToolViewer.ViewModels;
using PSDToolViewer.Views;

namespace PSDToolViewer;

public partial class App : Application
{
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _settings = AppSettings.Load();
        ThemeService.Initialize(_settings.Theme);
        MainViewModel.CleanupDragTemp();

        var updates = new UpdateService();
        MainWindow? window = null;
        var dialogs = new DialogService(() => window);
        var viewModel = new MainViewModel(_settings, dialogs, updates, Dispatcher);
        window = new MainWindow(viewModel, _settings);
        MainWindow = window;
        window.Show();

        var path = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (path is not null)
            _ = Dispatcher.BeginInvoke(async () => await viewModel.OpenPathAsync(path), DispatcherPriority.ApplicationIdle);

        if (_settings.CheckUpdatesOnStartup)
            _ = Dispatcher.BeginInvoke(async () => await viewModel.CheckForUpdatesAsync(manual: false), DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MainViewModel.CleanupDragTemp();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(MainWindow, $"予期しないエラーが発生しました。\n\n{e.Exception.Message}", "PSDTool Viewer",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
